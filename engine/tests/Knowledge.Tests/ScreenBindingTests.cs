using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// A screen's id bound to the app login that first listened with it (SB-P1-19; docs/dev/screens.md 3.2,
// docs/dev/memory.md 7.5, migration 029). Viewer sessions (app logins bound to a person, D-070) stand for the second app
// login that only app.viewers on lets reach the engine. The binding is which login a screen is, not what anyone reads,
// so each test runs with memory.scopes off and on. With one app login, every install's today, nothing is refused:
// ScreenTests, DashboardLayoutTests and ScreenScopeTests pass unchanged. All of it is DEMO data.
public sealed class ScreenBindingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    readonly ScreenWaits waits = new();
    NpgsqlDataSource Source => fixture.Source;
    ScreenStore Store(ScreenWaits? heard = null) => new(Source, heard ?? waits);
    static string NewScreen(string label) => label + "-" + Guid.NewGuid().ToString("N")[..16];
    // The shared app token's login (the household view's). A new token makes a new login and disables every other app
    // login, viewer sessions included, so each test takes it first.
    async Task<Guid> App() => (await new Principals(Source).SyncApp(Principals.NewToken(), default))!.Value;
    Task<Guid> ViewerOf(string person) => Scoped.Principal(Source, $"screen-binding-{person}-{Guid.NewGuid():N}", "app", person);
    Task<Guid> Assistant() => Scoped.Principal(Source, "screen-binding-" + Guid.NewGuid().ToString("N")[..8], "harness");

    static JsonElement Shows(string view) => JsonSerializer.SerializeToElement(new { view, layout = "hero" });
    static JsonElement Detail(string summary) => JsonSerializer.SerializeToElement(new { summary });
    static async Task<string> Refused(Func<Task> call) => (await Assert.ThrowsAsync<PostgresException>(call)).SqlState;
    async Task<Guid?> Bound(string screen) =>
        await Scoped.Scalar(Source, "SELECT bound_to FROM screens WHERE id=@s", ("s", screen)) is Guid login ? login : null;
    // The screen's whole row, to show a refused call wrote nothing.
    async Task<string?> Row(string screen) =>
        (string?)await Scoped.Scalar(Source, "SELECT row_to_json(s)::text FROM screens s WHERE id=@s", ("s", screen));
    async Task Again(Guid principal, string screen)
    {
        await using var cmd = Source.CreateCommand("SELECT listen_screen_again(@p,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("s", screen);
        await cmd.ExecuteScalarAsync();
    }

    // app_screens through the app's endpoint, as the app server calls it with that login: the status and the result.
    async Task<(int Status, JsonNode Content)> Call(Guid app, object arguments)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(Store()).BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "s", method = "tools/call",
            @params = new { name = "app_screens", arguments } }));
        context.Response.Body = new MemoryStream();
        await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, new AppHealth(fixture.Db, TestSettings.Use()), AdapterRegistry.Empty);
        return (context.Response.StatusCode, JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!);
    }

    async Task InEachMode(Func<Task> test)
    {
        foreach (var mode in new[] { "off", "on" })
        {
            await Scoped.Mode(Source, mode);
            try { await test(); }
            finally { await Scoped.Mode(Source, "off"); }
        }
    }

    [PostgresFact] public Task Another_login_never_listens_reports_sends_or_renames_as_a_screen_nor_is_told_its_settings() => InEachMode(async () =>
    {
        var household = await App(); var alex = await ViewerOf("alex"); var sam = await ViewerOf("sam"); var assistant = await Assistant();
        var store = Store();
        // Alex's phone, made by her session's first listen, keeps her private Home dashboard; the kitchen is the
        // household view's.
        var home = Guid.NewGuid().ToString();
        var (phone, kitchen) = (NewScreen("alex-phone"), NewScreen("kitchen"));
        var heard = await store.Listen(alex, phone, "DEMO phone", TimeSpan.Zero, Shows("home"),
            JsonSerializer.SerializeToElement(new { layout = "hero", home = new { dashboard = home } }), default);
        Assert.Equal(home, (string?)heard["screen"]!["settings"]!["home"]!["dashboard"]);
        await store.Listen(household, kitchen, "DEMO kitchen", TimeSpan.Zero, Shows("home"), null, default);
        Assert.Equal((alex, household), ((await Bound(phone))!.Value, (await Bound(kitchen))!.Value));
        Guid[] others = [sam, household];

        // No other login listens as her phone, at a first look or after a wait: refused before anything is written or
        // claimed, and its answer names nothing of the screen.
        var before = await Row(phone);
        var sent = await store.Send(assistant, phone, "home", [], null, TimeSpan.Zero, default);
        var command = Guid.Parse((string)sent["id"]!);
        foreach (var other in others)
        {
            Assert.Equal("42501", await Refused(() => store.Listen(other, phone, "DEMO taken", TimeSpan.Zero, Shows("star"),
                JsonSerializer.SerializeToElement(new { layout = "band" }), default)));
            Assert.Equal("42501", await Refused(() => Again(other, phone)));
            var (status, answer) = await Call(other, new { op = "listen", screen = phone, name = "DEMO taken", hold = 0 });
            Assert.Equal((403, "forbidden"), (status, (string?)answer["error"]!["code"]));
            Assert.DoesNotContain(home, answer.ToJsonString());
        }
        Assert.Equal(before, await Row(phone));
        Assert.Equal("pending", (string?)(await store.Command(assistant, command, default))!["result"]);

        // Her session takes the command; no other login reports for it.
        Assert.Equal(command.ToString(), (string?)(await store.Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default))["command"]!["id"]);
        foreach (var other in others)
        {
            Assert.Equal("42501", await Refused(() => store.Done(other, phone, command, "shown", Detail("DEMO not mine"), Shows("star"), default)));
            Assert.Equal(403, (await Call(other, new { op = "done", screen = phone, command = command.ToString(), result = "failed" })).Status);
        }
        Assert.Equal("delivered", (string?)(await store.Command(assistant, command, default))!["result"]);
        await store.Done(alex, phone, command, "shown", Detail("DEMO shown"), null, default);
        Assert.Equal("shown", (string?)(await store.Command(assistant, command, default))!["result"]);

        // No other login renames it, or is answered its settings; she is.
        foreach (var other in others)
        {
            Assert.Equal("42501", await Refused(() => store.Rename(other, phone, "DEMO renamed", default)));
            var (status, answer) = await Call(other, new { op = "rename", screen = phone, name = "DEMO renamed" });
            Assert.Equal(403, status);
            Assert.DoesNotContain(home, answer.ToJsonString());
        }
        var renamed = await store.Rename(alex, phone, "DEMO Alex's phone", default);
        Assert.Equal(("DEMO Alex's phone", home), ((string?)renamed["name"], (string?)renamed["settings"]!["home"]!["dashboard"]));

        // No other login sends as her phone; she sends as it, and not as the kitchen, which she neither listens as nor
        // renames.
        foreach (var other in others)
        {
            Assert.Equal("42501", await Refused(() => store.Send(other, kitchen, "home", [], phone, TimeSpan.Zero, default)));
            Assert.Equal(403, (await Call(other, new { op = "send", screen = phone, to = kitchen, action = "home" })).Status);
        }
        var fromPhone = await store.Send(alex, kitchen, "layout", new JsonObject { ["layout"] = "band" }, phone, TimeSpan.Zero, default);
        var sender = fromPhone["sender"]!;
        Assert.Equal(("screen", phone, "DEMO Alex's phone"), ((string?)sender["kind"], (string?)sender["screen"], (string?)sender["name"]));
        Assert.Equal("42501", await Refused(() => store.Send(alex, phone, "home", [], kitchen, TimeSpan.Zero, default)));
        Assert.Equal("42501", await Refused(() => store.Listen(alex, kitchen, "DEMO kitchen", TimeSpan.Zero, null, null, default)));
        Assert.Equal("42501", await Refused(() => store.Rename(alex, kitchen, "DEMO mine", default)));
        // The household view goes on as before on its own screen, and takes what she sent it.
        var kitchens = await store.Listen(household, kitchen, "DEMO kitchen", TimeSpan.Zero, null, null, default);
        Assert.Equal((string?)fromPhone["id"], (string?)kitchens["command"]!["id"]);
        Assert.Equal("42501", await Refused(() => store.Done(alex, kitchen, Guid.Parse((string)fromPhone["id"]!), "shown", null, null, default)));
        await store.Done(household, kitchen, Guid.Parse((string)fromPhone["id"]!), "shown", null, null, default);
        Assert.Equal("DEMO kitchen", (string?)(await store.Rename(household, kitchen, "DEMO kitchen", default))["name"]);

        // Her session ended: the phone stays hers, so no other login is told its settings even now.
        await Scoped.Execute(Source, "UPDATE principals SET enabled=false WHERE id=@p", ("p", alex));
        foreach (var other in others)
        {
            Assert.Equal("42501", await Refused(() => store.Listen(other, phone, "DEMO taken", TimeSpan.Zero, null, null, default)));
            Assert.Equal("42501", await Refused(() => store.Rename(other, phone, "DEMO renamed", default)));
        }
        Assert.Equal(alex, await Bound(phone));
    });

    [PostgresFact] public Task The_household_views_screens_stay_the_household_views_under_a_new_app_token() => InEachMode(async () =>
    {
        var first = await App(); var assistant = await Assistant();
        var store = Store();
        var home = Guid.NewGuid().ToString();
        var (kitchen, hallway) = (NewScreen("kitchen"), NewScreen("hallway"));
        await store.Listen(first, kitchen, "DEMO kitchen", TimeSpan.Zero, Shows("home"),
            JsonSerializer.SerializeToElement(new { home = new { dashboard = home } }), default);
        await store.Listen(first, hallway, "DEMO hallway", TimeSpan.Zero, Shows("home"), null, default);
        // A new app token: its login is another principal, and the old one is disabled.
        var next = await App();
        Assert.NotEqual(first, next);
        Assert.Equal(false, await Scoped.Scalar(Source, "SELECT enabled FROM principals WHERE id=@p", ("p", first)));

        // The household view's screens are its new login's: it listens, takes their commands, reports, sends and renames.
        Assert.Equal(home, (string?)(await store.Listen(next, kitchen, "DEMO kitchen", TimeSpan.Zero, null, null, default))["screen"]!["settings"]!["home"]!["dashboard"]);
        var sent = await store.Send(assistant, kitchen, "home", [], null, TimeSpan.Zero, default);
        Assert.Equal((string?)sent["id"], (string?)(await store.Listen(next, kitchen, "DEMO kitchen", TimeSpan.Zero, null, null, default))["command"]!["id"]);
        Assert.Equal(true, (bool?)(await store.Done(next, kitchen, Guid.Parse((string)sent["id"]!), "shown", Detail("DEMO home"), Shows("home"), default))["ok"]);
        Assert.Equal(hallway, (string?)(await store.Send(next, kitchen, "home", [], hallway, TimeSpan.Zero, default))["sender"]!["screen"]);
        Assert.Equal(home, (string?)(await store.Rename(next, kitchen, "DEMO kitchen", default))["settings"]!["home"]!["dashboard"]);
        // It stays bound to the login that first listened with it.
        Assert.Equal(first, await Bound(kitchen));

        // A viewer session made since is not the household view: the kitchen is not its.
        var alex = await ViewerOf("alex");
        Assert.Equal("42501", await Refused(() => store.Listen(alex, kitchen, "DEMO kitchen", TimeSpan.Zero, null, null, default)));
        Assert.Equal("42501", await Refused(() => store.Rename(alex, kitchen, "DEMO kitchen", default)));
    });

    [PostgresFact] public Task A_screen_from_before_the_binding_is_the_household_views_and_bound_by_its_next_listen() => InEachMode(async () =>
    {
        var household = await App(); var alex = await ViewerOf("alex"); var sam = await ViewerOf("sam");
        var store = Store();
        var (hallway, den, study) = (NewScreen("hallway"), NewScreen("den"), NewScreen("study"));
        foreach (var screen in new[] { hallway, den, study })
            await store.Listen(household, screen, "DEMO " + screen[..5], TimeSpan.Zero, Shows("home"), null, default);
        // As migration 029 finds them: no login bound.
        await Scoped.Execute(Source, "UPDATE screens SET bound_to=NULL WHERE id=ANY(@s)", ("s", new[] { hallway, den, study }));

        // Until a listen binds one, it is the household view's alone, as every screen made before 029 was: no viewer
        // session listens as it (at a first look or after a wait), reports for it, sends as it or renames it, and a
        // refused listen binds nothing.
        Assert.Equal("42501", await Refused(() => store.Rename(alex, hallway, "DEMO mine", default)));
        Assert.Equal("42501", await Refused(() => store.Send(alex, den, "home", [], hallway, TimeSpan.Zero, default)));
        Assert.Equal("42501", await Refused(() => store.Listen(alex, hallway, "DEMO hall", TimeSpan.Zero, Shows("home"), null, default)));
        Assert.Equal("42501", await Refused(() => Again(sam, study)));
        Assert.Equal("DEMO hall", (string?)(await store.Rename(household, hallway, "DEMO hall", default))["name"]);
        Assert.Null(await Bound(hallway));
        Assert.Null(await Bound(study));

        // The household view's next listen binds it, and is never refused for it: a first look or one after a wait.
        Assert.Null((await store.Listen(household, hallway, "DEMO hall", TimeSpan.Zero, Shows("home"), null, default))["command"]);
        Assert.Equal(household, await Bound(hallway));
        await Again(household, study);
        Assert.Equal(household, await Bound(study));

        // From then on each is that login's: the household view's, under a new app token too, and no session's.
        Assert.Equal("42501", await Refused(() => store.Listen(sam, hallway, "DEMO hall", TimeSpan.Zero, null, null, default)));
        Assert.Equal("42501", await Refused(() => store.Listen(alex, study, "DEMO study", TimeSpan.Zero, null, null, default)));
        Assert.Equal("DEMO study", (string?)(await store.Rename(household, study, "DEMO study", default))["name"]);
        Assert.Null(await Bound(den));
    });

    // The 100-screen cap counts each login's screens apart: another login's new screens (a session's, or the household
    // view's, which every front end can reach) never push a session's screen out, so its id is never free to take.
    [PostgresFact] public async Task Another_logins_new_screens_never_push_a_bound_screen_out_to_take_its_id()
    {
        var household = await App(); var alex = await ViewerOf("alex"); var sam = await ViewerOf("sam");
        var store = Store();
        var phone = NewScreen("alex-phone");
        await store.Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default);
        async Task Fresh(Guid login, string label)
        {
            await using var cmd = Source.CreateCommand("SELECT listen_screen(@p,@s,@n,NULL,NULL)::text");
            cmd.Parameters.AddWithValue("p", login);
            cmd.Parameters.AddWithValue("s", NewScreen(label));
            cmd.Parameters.AddWithValue("n", "DEMO " + label);
            await cmd.ExecuteScalarAsync();
        }
        async Task<long> Of(Guid login) => (long)(await Scoped.Scalar(Source, "SELECT count(*) FROM screens WHERE bound_to=@p", ("p", login)))!;
        for (var i = 0; i < 101; i++) { await Fresh(sam, "sam-" + i); await Fresh(household, "house-" + i); }
        Assert.Equal(alex, await Bound(phone));
        Assert.Equal("42501", await Refused(() => store.Listen(sam, phone, "DEMO taken", TimeSpan.Zero, null, null, default)));
        Assert.Equal("42501", await Refused(() => store.Listen(household, phone, "DEMO taken", TimeSpan.Zero, null, null, default)));
        Assert.Null((await store.Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default))["command"]);
        // Each login keeps its own hundred, its own unseen longest making way.
        Assert.Equal((100L, 1L), (await Of(sam), await Of(alex)));
        Assert.True(await Of(household) <= 100);
    }

    // A listen counts as its screen listening only once its first look is answered: while another login's listen waits
    // on the database, or is given up by its caller then, the screen's listening is as it was.
    [PostgresFact] public async Task A_listen_waiting_on_the_database_or_given_up_never_changes_whether_its_screen_listens()
    {
        var household = await App(); var alex = await ViewerOf("alex");
        var phone = NewScreen("alex-phone");
        await Store().Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default);
        var unheard = new ScreenWaits();
        async Task Blocked()
        {
            for (var i = 0; i < 100; i++)
            {
                if ((long)(await Scoped.Scalar(Source, "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'SELECT listen_screen(%'"))! > 0) return;
                await Task.Delay(50);
            }
            Assert.Fail("the listen never waited on the screen's row");
        }
        foreach (var givenUp in new[] { false, true })
        {
            await using var c = await Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT 1 FROM screens WHERE id=@s FOR UPDATE", c, tx))
            {
                hold.Parameters.AddWithValue("s", phone);
                await hold.ExecuteScalarAsync();
            }
            using var caller = new CancellationTokenSource();
            var listen = Store(unheard).Listen(household, phone, "DEMO taken", TimeSpan.Zero, null, null, caller.Token);
            await Blocked();
            // Waits that have heard no listen of the phone: the waiting listen does not make it listen.
            Assert.Equal(givenUp, unheard.Listening(phone));
            if (givenUp)
            {
                // Given up while it waits (the row is still held, so only the cancel ends it): the phone's own
                // login's listening goes on.
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
                await tx.RollbackAsync();
                Assert.True(unheard.Listening(phone));
            }
            else
            {
                await tx.RollbackAsync();
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => listen)).SqlState);
                Assert.False(unheard.Listening(phone));
                // Her own listen, heard by these waits, for the round given up.
                await Store(unheard).Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default);
            }
        }
    }

    [PostgresFact] public async Task Two_logins_making_one_new_screen_at_once_bind_it_to_one_of_them()
    {
        await App(); var alex = await ViewerOf("alex"); var sam = await ViewerOf("sam");
        // Through the engine (which reads a new screen's name first, under the lock a new screen takes), and straight
        // through listen_screen, whose insert then meets the other's row.
        for (var round = 0; round < 10; round++)
        {
            var screen = NewScreen("shared");
            async Task<Guid?> Try(Guid login)
            {
                try
                {
                    if (round % 2 == 0) await Store().Listen(login, screen, "DEMO shared", TimeSpan.Zero, Shows("home"), null, default);
                    else
                    {
                        await using var cmd = Source.CreateCommand("SELECT listen_screen(@p,@s,'DEMO shared','{\"view\":\"home\"}'::jsonb,NULL)::text");
                        cmd.Parameters.AddWithValue("p", login);
                        cmd.Parameters.AddWithValue("s", screen);
                        await cmd.ExecuteScalarAsync();
                    }
                    return login;
                }
                catch (PostgresException e) when (e.SqlState == "42501") { return null; }
            }
            var made = (await Task.WhenAll(Try(alex), Try(sam))).OfType<Guid>().ToArray();
            Assert.Single(made);
            Assert.Equal(made[0], await Bound(screen));
            Assert.Equal(made[0], await Scoped.Scalar(Source, "SELECT shown_by FROM screens WHERE id=@s", ("s", screen)));
        }
    }

    [PostgresFact] public async Task A_refused_listen_never_shows_the_screen_listening()
    {
        var household = await App(); var alex = await ViewerOf("alex");
        var phone = NewScreen("alex-phone");
        await Store().Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default);
        // Waits that have heard no listen of the phone: the household view's refused listen leaves it not listening, so
        // control_screen never takes it for a screen that listens.
        var unheard = new ScreenWaits();
        await Refused(() => Store(unheard).Listen(household, phone, "DEMO taken", TimeSpan.Zero, null, null, default));
        Assert.False(unheard.Listening(phone));
        // Nor does it end the listening of the phone's own login.
        await Store(unheard).Listen(alex, phone, "DEMO phone", TimeSpan.Zero, null, null, default);
        Assert.True(unheard.Listening(phone));
        await Refused(() => Store(unheard).Listen(household, phone, "DEMO taken", TimeSpan.Zero, null, null, default));
        Assert.True(unheard.Listening(phone));
    }
}
