using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Documents;
using Knowledge.Cli;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Knowledge.Tests;

// Viewer sessions in the app (SB-P1-11; docs/dev/memory.md section 3, D-046 and D-070): app.viewers off by
// default and on refused by this version; an operator's one-time code opens one session for one person, whose
// credential is an app principal bound to that person; the shared app token alone reads the household view whatever is
// sent beside it; a suspended person's session gets 401 at the door. Every person, code and token is fictional.
public sealed class ViewerSettingsTests
{
    const string Head = "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex }, { id: sam, name: Sam }]\n";

    [Fact] public void Viewer_sessions_are_off_unless_rhythm_yaml_says_on_and_this_version_refuses_on()
    {
        Assert.False(ViewerSettings.From(RhythmConfig.Parse(Head)).On);
        Assert.False(ViewerSettings.From(RhythmConfig.Parse(Head + "app: { viewers: off }\n")).On);
        Assert.False(ViewerSettings.From(RhythmConfig.Parse(Head + "app: { viewers: }\n")).On);
        Assert.Contains("app.viewers: on is refused", Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + "app: { viewers: on }\n")).Message);
        foreach (var wrong in new[] { "true", "On", "yes", "1" })
            Assert.Contains("app.viewers is off or on", Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + $"app: {{ viewers: {wrong} }}\n")).Message);
        // A list is no setting either: the file does not load.
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + "app: { viewers: [on] }\n"));
        // Only the reviewed change that follows the owner's approval accepts it; until then tests pass their own.
        Assert.False(ViewerSettings.Accepted);
        Assert.NotNull(new ViewerSettings(true).Refusal());
        Assert.Null(new ViewerSettings(true).Refusal(accepted: true));
        Assert.Null(ViewerSettings.Off.Refusal());
        // The example settings and the settings schema say off.
        Assert.False(ViewerSettings.From(RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"))).On);
    }

    [Fact] public void A_code_is_twelve_characters_typed_any_way_and_nothing_else_reads_as_one()
    {
        var code = ViewerSessions.NewCode();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);
        Assert.NotEqual(code, ViewerSessions.NewCode());
        var plain = code.Replace("-", "");
        Assert.Equal(plain, ViewerSessions.Normal(code));
        Assert.Equal(plain, ViewerSessions.Normal(" " + plain.ToLowerInvariant()[..6] + " - " + plain[6..] + " "));
        Assert.Equal("0123456789AB", ViewerSessions.Normal("o123456789ab"));
        Assert.Equal("1111ABCDEFGH", ViewerSessions.Normal("iLl1-ABCD-EFGH"));
        foreach (var wrong in new[] { "", "ABCD-EFGH-JKM", "ABCD-EFGH-JKMNP", "ABCD-EFGH-JKMU", "ABCD/EFGH/JKMN", new string('A', 41), null })
            Assert.Null(ViewerSessions.Normal(wrong));
        Assert.True(ViewerSessions.Fits(Principals.NewToken()));
        Assert.False(ViewerSessions.Fits(Principals.NewToken()[..42]));
        Assert.False(ViewerSessions.Fits(Principals.NewToken() + "="));
    }

    [Fact] public void The_viewer_tool_takes_exactly_each_operations_arguments()
    {
        static JsonElement Call(object arguments) => JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = "v", method = "tools/call",
            @params = new { name = AppEndpoint.Viewer, arguments } });
        var credential = Principals.NewToken();
        Assert.True(AppEndpoint.Allowed(Call(new { op = "enrol", code = "ABCD-EFGH-JKMN", credential })));
        Assert.True(AppEndpoint.Allowed(Call(new { op = "whoami" })));
        Assert.True(AppEndpoint.Allowed(Call(new { op = "end" })));
        Assert.False(AppEndpoint.Allowed(Call(new { op = "enrol", code = "ABCD-EFGH-JKMN" })));
        Assert.False(AppEndpoint.Allowed(Call(new { op = "enrol", code = "ABCD-EFGH-JKMN", credential = "short" })));
        Assert.False(AppEndpoint.Allowed(Call(new { op = "enrol", code = "ABCD-EFGH-JKMN", credential, person = "alex" })));
        // Nothing names a person beside the shared token: whoami and end take nothing but op.
        Assert.False(AppEndpoint.Allowed(Call(new { op = "whoami", person = "alex" })));
        Assert.False(AppEndpoint.Allowed(Call(new { op = "end", credential })));
        Assert.False(AppEndpoint.Allowed(Call(new { op = "become", person = "alex" })));
        Assert.False(AppEndpoint.Allowed(Call(new { })));
    }

    // Where an upload in the app lands (SB-P1-20; memory.md section 5): in Family when it chooses it; with scopes on, in
    // a viewer session's person's own scope; else (null) where the documents adapter puts every upload today, which is
    // the household view's and, with scopes off, every session's too: a binding only records who saved it then.
    [Fact] public void An_upload_lands_in_Family_when_chosen_else_in_a_sessions_own_scope_with_scopes_on()
    {
        Assert.Equal("alex", ViewerSessions.Lands(scopesOn: true, person: "alex", family: false));
        Assert.Equal(RhythmConfig.Household, ViewerSessions.Lands(scopesOn: true, person: "alex", family: true));
        Assert.Null(ViewerSessions.Lands(scopesOn: true, person: null, family: false));
        Assert.Equal(RhythmConfig.Household, ViewerSessions.Lands(scopesOn: true, person: null, family: true));
        Assert.Null(ViewerSessions.Lands(scopesOn: false, person: "alex", family: false));
        Assert.Equal(RhythmConfig.Household, ViewerSessions.Lands(scopesOn: false, person: "alex", family: true));
        Assert.Null(ViewerSessions.Lands(scopesOn: false, person: null, family: false));
    }

    // The Family choice is an upload's only while app.viewers is on: with it off the tool takes exactly name and content,
    // as before, and family is outside its schema.
    [Fact] public void An_upload_takes_its_Family_choice_only_while_viewer_sessions_are_on()
    {
        static JsonElement Call(string arguments) => JsonDocument.Parse("""{"jsonrpc":"2.0","id":"u","method":"tools/call","params":{"name":"app_upload_document","arguments":"""
            + arguments + "}}").RootElement;
        const string File = "\"name\":\"DEMO a.txt\",\"content\":\"YQ==\"";
        foreach (var viewers in new[] { false, true })
        {
            Assert.True(AppEndpoint.Allowed(Call("{" + File + "}"), null, viewers));
            Assert.False(AppEndpoint.Allowed(Call("{" + File + ",\"person\":\"alex\"}"), null, viewers));
            Assert.False(AppEndpoint.Allowed(Call("{\"name\":\"DEMO a.txt\",\"family\":true}"), null, viewers));
        }
        Assert.False(AppEndpoint.Allowed(Call("{" + File + ",\"family\":true}")));
        Assert.False(AppEndpoint.Allowed(Call("{" + File + ",\"family\":false}")));
        Assert.True(AppEndpoint.Allowed(Call("{" + File + ",\"family\":true}"), null, viewers: true));
        Assert.True(AppEndpoint.Allowed(Call("{" + File + ",\"family\":false}"), null, viewers: true));
        foreach (var wrong in new[] { "\"family\":\"yes\"", "\"family\":1", "\"family\":null", "\"family\":true,\"family\":true", "\"family\":true,\"scope\":\"private\"" })
            Assert.False(AppEndpoint.Allowed(Call("{" + File + "," + wrong + "}"), null, viewers: true), wrong);
    }

    [Fact] public async Task With_viewer_sessions_off_the_tool_answers_as_one_this_role_does_not_have()
    {
        var config = TestSettings.Use();
        async Task<(int, string)> Ask(string name)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "v", method = "tools/call",
                @params = new { name, arguments = new { op = "whoami" } } }));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(Guid.NewGuid(), "app", "app", null), null!, new AppHealth(null!, config), AdapterRegistry.Empty);
            var answer = JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!;
            return (context.Response.StatusCode, answer["result"]!["structuredContent"]!.ToJsonString());
        }
        Assert.Equal(await Ask("app_unknown"), await Ask(AppEndpoint.Viewer));
        Assert.Equal(403, (await Ask(AppEndpoint.Viewer)).Item1);
    }
}

// The engine's door (Mcp/Perimeter.cs) with the real app endpoint behind it, on an in-memory server: viewer sessions
// on (a ViewerSessions service), this database's Authenticate, and the map host when a test reads the map.
internal sealed class ViewerDoor : IAsyncDisposable
{
    WebApplication app = null!;
    HttpClient client = null!;

    public static async Task<ViewerDoor> Open(Database db, RelationshipHost? host = null, AdapterRegistry? registry = null)
    {
        var config = TestSettings.Use();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ViewerSessions(db.Source));
        var app = builder.Build();
        var health = new AppHealth(db, config);
        app.Use(new Perimeter(["engine"], () => true, (t, c) => db.Authenticate(t, true, c), new(
            App: (c, caller) => AppEndpoint.Handle(c, caller, host!, health, registry ?? AdapterRegistry.Empty),
            Ingest: (c, _) => c.Response.WriteAsync("ingest"), Document: (c, _, _) => c.Response.WriteAsync("document"))).Handle);
        await app.StartAsync();
        return new ViewerDoor { app = app, client = app.GetTestClient() };
    }

    // One tools/call with this bearer token and any extra headers; the status and structuredContent.
    public async Task<(HttpStatusCode Status, JsonNode? Content)> Call(string token, string tool, object arguments, params (string, string)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://engine/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "v", method = "tools/call", @params = new { name = tool, arguments } }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var content = text.Length == 0 ? null : JsonNode.Parse(text)?["result"]?["structuredContent"];
        return (response.StatusCode, content);
    }

    // The tools this token is offered.
    public async Task<string[]> Tools(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://engine/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":"v","method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await client.SendAsync(request);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return [.. answer["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!)];
    }

    public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
}

public sealed class ViewerSessionDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly string Mcp = Schemas.Folder("mcp");

    async Task<(Guid Id, string Token)> SharedApp()
    {
        var token = Principals.NewToken();
        return ((await new Principals(fixture.Source).SyncApp(token, default))!.Value, token);
    }

    [PostgresFact] public async Task A_code_opens_one_session_once_for_its_person_and_nothing_else_opens_one()
    {
        var sessions = new ViewerSessions(fixture.Source);
        var (app, _) = await SharedApp();
        var harness = await Scoped.Principal(fixture.Source, "viewer-test-harness", "harness", "alex");

        var (code, expires) = await sessions.Invite("alex", 15, default);
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        // Only its SHA-256 is kept, never the code.
        Assert.Equal(0L, await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM viewer_codes WHERE code_hash=@c OR code_hash=@p",
            ("c", code), ("p", ViewerSessions.Normal(code))));
        // A harness token, even one bound to the code's person, never opens a session; nor does a wrong code.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.Enrol(harness, code, Principals.NewToken(), default));
        Assert.Null(await sessions.Enrol(app, ViewerSessions.NewCode(), Principals.NewToken(), default));
        Assert.Null(await sessions.Enrol(app, "not a code", Principals.NewToken(), default));

        // The code as someone types it opens one session, for its person, once.
        var credential = Principals.NewToken();
        Assert.Equal("alex", await sessions.Enrol(app, code.Replace("-", " ").ToLowerInvariant(), credential, default));
        var caller = await fixture.Db.Authenticate(credential, true, default);
        Assert.NotNull(caller);
        Assert.Equal("app", caller.Role);
        Assert.StartsWith(ViewerSessions.Prefix + "alex-", caller.Name);
        Assert.Equal("alex", await Scoped.Scalar(fixture.Source, "SELECT person_id FROM principals WHERE id=@id", ("id", caller.Id)));
        Assert.Equal("alex", await sessions.Person(caller.Id, default));
        Assert.Null(await sessions.Person(app, default));
        Assert.Null(await sessions.Enrol(app, code, Principals.NewToken(), default));
        // A session's credential never opens another session, and a credential already registered is never taken over.
        var (second, _) = await sessions.Invite("alex", 5, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.Enrol(caller.Id, second, Principals.NewToken(), default));
        Assert.Null(await sessions.Enrol(app, second, credential, default));
        Assert.Equal(1L, await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM principals WHERE token_hash=@h", ("h", Hashing.Hash(credential))));

        // An expired code, and a code whose person was suspended since, open nothing; no code is made for a suspended,
        // former or unknown person, or for longer than an hour.
        var (stale, _) = await sessions.Invite("sam", 1, default);
        await Scoped.Execute(fixture.Source, "UPDATE viewer_codes SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour' WHERE used_at IS NULL AND person_id='sam'");
        Assert.Null(await sessions.Enrol(app, stale, Principals.NewToken(), default));
        var (pending, _) = await sessions.Invite("sam", 15, default);
        var principals = new Principals(fixture.Source);
        await principals.Suspend("sam", default);
        try
        {
            Assert.Null(await sessions.Enrol(app, pending, Principals.NewToken(), default));
            Assert.Contains("suspended", (await Assert.ThrowsAsync<ArgumentException>(() => sessions.Invite("sam", 15, default))).Message);
        }
        finally { await principals.Unsuspend("sam", default); await sessions.Revoke(null, "sam", default); }
        Assert.Contains("not a person", (await Assert.ThrowsAsync<ArgumentException>(() => sessions.Invite("jo", 15, default))).Message);
        Assert.Contains("not a person", (await Assert.ThrowsAsync<ArgumentException>(() => sessions.Invite(RhythmConfig.Household, 15, default))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => sessions.Invite("alex", 61, default));
        await Assert.ThrowsAsync<ArgumentException>(() => sessions.Invite("alex", 0, default));
        // The database refuses a code that would last more than an hour, however it is written.
        await Assert.ThrowsAsync<PostgresException>(() => Scoped.Execute(fixture.Source,
            "INSERT INTO viewer_codes(code_hash,person_id,expires_at) VALUES('x','alex',now()+interval '2 hours')"));
    }

    [PostgresFact] public async Task A_session_speaks_for_its_person_alone_and_a_suspended_persons_session_gets_401()
    {
        var sessions = new ViewerSessions(fixture.Source);
        var (_, shared) = await SharedApp();
        await using var door = await ViewerDoor.Open(fixture.Db);
        // With viewer sessions on, every app principal is offered the tool; the household view's whoami is no one.
        Assert.Contains(AppEndpoint.Viewer, await door.Tools(shared));
        var (code, _) = await sessions.Invite("alex", 15, default);
        var credential = Principals.NewToken();
        var (status, enrolled) = await door.Call(shared, AppEndpoint.Viewer, new { op = "enrol", code, credential });
        Assert.Equal((HttpStatusCode.OK, "alex"), (status, (string?)enrolled!["person"]));
        Schemas.Valid(Mcp, "app_viewer.output.schema.json", enrolled);
        var (_, refused) = await door.Call(shared, AppEndpoint.Viewer, new { op = "enrol", code, credential = Principals.NewToken() });
        Assert.Null((string?)refused!["person"]);
        Schemas.Valid(Mcp, "app_viewer.output.schema.json", refused);

        // The shared token with a person sent beside it, as a header, a cookie or an argument, is still the household view.
        var forged = new[] { ("X-Rhythm-Person", "alex"), ("Cookie", "person=alex; rhythm-viewer=" + credential), ("X-Forwarded-User", "alex") };
        var (_, household) = await door.Call(shared, AppEndpoint.Viewer, new { op = "whoami" }, forged);
        Assert.Null((string?)household!["person"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await door.Call(shared, AppEndpoint.Viewer, new { op = "whoami", person = "alex" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await door.Call(shared, AppEndpoint.Graph, new { person = "alex" })).Status);
        // The household view can't sign itself out: it is every front end's.
        Assert.Equal(HttpStatusCode.Forbidden, (await door.Call(shared, AppEndpoint.Viewer, new { op = "end" })).Status);

        // The session's credential reaches the app's tools and speaks for its person; it can't register another session.
        var (_, who) = await door.Call(credential, AppEndpoint.Viewer, new { op = "whoami" });
        Assert.Equal("alex", (string?)who!["person"]);
        Schemas.Valid(Mcp, "app_viewer.output.schema.json", who);
        var (next, _) = await sessions.Invite("alex", 15, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await door.Call(credential, AppEndpoint.Viewer, new { op = "enrol", code = next, credential = Principals.NewToken() })).Status);

        // Suspending the person makes the session's credential fail at the door, and lifting it does not bring it back.
        var principals = new Principals(fixture.Source);
        var (_, disabled) = await principals.Suspend("alex", default);
        try
        {
            Assert.True(disabled >= 1);
            Assert.Equal(HttpStatusCode.Unauthorized, (await door.Call(credential, AppEndpoint.Viewer, new { op = "whoami" })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await door.Call(credential, AppEndpoint.Graph, new { })).Status);
        }
        finally { await principals.Unsuspend("alex", default); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await door.Call(credential, AppEndpoint.Viewer, new { op = "whoami" })).Status);

        // Signing out ends a session for good.
        var again = Principals.NewToken();
        Assert.Equal("alex", (string?)(await door.Call(shared, AppEndpoint.Viewer, new { op = "enrol", code = next, credential = again })).Content!["person"]);
        var (_, ended) = await door.Call(again, AppEndpoint.Viewer, new { op = "end" });
        Assert.True((bool)ended!["ended"]!);
        Schemas.Valid(Mcp, "app_viewer.output.schema.json", ended);
        Assert.Equal(HttpStatusCode.Unauthorized, (await door.Call(again, AppEndpoint.Viewer, new { op = "whoami" })).Status);
        // The shared token goes on reading as before.
        Assert.Equal(HttpStatusCode.OK, (await door.Call(shared, AppEndpoint.Viewer, new { op = "whoami" })).Status);
    }

    [PostgresFact] public async Task The_operator_invites_lists_and_revokes_sessions_and_codes_only_while_viewer_sessions_are_on()
    {
        var sessions = new ViewerSessions(fixture.Source);
        var (app, _) = await SharedApp();
        var deploy = new DeployFolder(Path.GetTempPath());
        async Task<(int Code, string Output)> Run(ViewerSettings? settings, bool accepted, params string[] args)
        {
            var o = new StringWriter();
            var code = await ViewerCommand.Run(deploy, args, o, sessions, settings, accepted);
            return (code, o.ToString());
        }
        // Off (every install's setting), and on as this version refuses it: no code is made.
        var before = await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM viewer_codes");
        var (off, offSays) = await Run(ViewerSettings.Off, false, "invite", "alex");
        Assert.Equal(1, off);
        Assert.Contains("viewer sessions are off", offSays);
        var (refused, refusedSays) = await Run(new ViewerSettings(true), false, "invite", "alex");
        Assert.Equal(1, refused);
        Assert.Contains("refused by this version", refusedSays);
        Assert.Equal(before, await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM viewer_codes"));

        // On (tests only): a code, printed once, then a session made with it; list shows both, by person, never the code.
        var (made, says) = await Run(new ViewerSettings(true), true, "invite", "sam", "--minutes", "30");
        Assert.Equal(0, made);
        var code = System.Text.RegularExpressions.Regex.Match(says, "Code for sam: ([0-9A-Z-]{14})").Groups[1].Value;
        var (_, listed) = await Run(null, false, "list");
        Assert.Contains("code waiting\tsam", listed);
        Assert.DoesNotContain(code, listed);
        Assert.Equal("sam", await sessions.Enrol(app, code, Principals.NewToken(), default));
        var (_, opened) = await Run(null, false, "list");
        var name = System.Text.RegularExpressions.Regex.Match(opened, $"({ViewerSessions.Prefix}sam-[0-9a-f]{{8}})\tsam\t").Groups[1].Value;
        Assert.NotEmpty(name);
        Assert.Contains((await new Principals(fixture.Source).List(default)), t => t.Name == name && t.Role == "app" && t.Person == "sam");

        // revoke by name ends that one; revoke --person ends every session of theirs and their unused codes, and nothing else.
        var (second, _) = await sessions.Invite("sam", 15, default);
        Assert.Equal("sam", await sessions.Enrol(app, second, Principals.NewToken(), default));
        await sessions.Invite("sam", 15, default);
        var harness = await Scoped.Principal(fixture.Source, "viewer-test-sam-client", "harness", "sam");
        Assert.Contains("Ended 1 viewer session(s).", (await Run(null, false, "revoke", name)).Output);
        Assert.Contains("Ended 0 viewer session(s).", (await Run(null, false, "revoke", "viewer-test-sam-client")).Output);
        Assert.Contains("Ended 1 viewer session(s) of sam and removed 1 unused code(s).", (await Run(null, false, "revoke", "--person", "sam")).Output);
        Assert.Equal(0L, await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM principals WHERE person_id='sam' AND role='app' AND enabled"));
        Assert.Equal(true, await Scoped.Scalar(fixture.Source, "SELECT enabled FROM principals WHERE id=@id", ("id", harness)));
        Assert.Equal(true, await Scoped.Scalar(fixture.Source, "SELECT enabled FROM principals WHERE id=@id", ("id", app)));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(null, false, "revoke"));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(null, false, "invite"));
    }

    // What ends a session besides its person and the operator: app.viewers off (the door refuses every session's
    // credential, the shared token reading on), its 30 days (at the engine as in the browser), and replacing the app
    // token's file, from which the app's server derived it.
    [PostgresFact] public async Task A_session_ends_with_viewer_sessions_off_after_its_lifetime_and_with_the_app_token()
    {
        var sessions = new ViewerSessions(fixture.Source);
        var principals = new Principals(fixture.Source);
        var (app, shared) = await SharedApp();
        async Task<string> Open(Guid by)
        {
            var (code, _) = await sessions.Invite("alex", 15, default);
            var credential = Principals.NewToken();
            Assert.Equal("alex", await sessions.Enrol(by, code, credential, default));
            return credential;
        }
        Task<object?> Enabled(string credential) =>
            Scoped.Scalar(fixture.Source, "SELECT enabled FROM principals WHERE token_hash=@h", ("h", Hashing.Hash(credential)));

        var credential = await Open(app);
        Assert.NotNull(await fixture.Db.Authenticate(credential, true, default));
        Assert.Null(await fixture.Db.Authenticate(credential, false, default));
        Assert.Equal("app", (await fixture.Db.Authenticate(shared, false, default))!.Role);
        // The door as the engine's start wires it (Engine.Door): with app.viewers off, every install's setting, the
        // session's credential is refused and the shared token admitted; only settings with it on admit the session. The
        // check that is not told the setting fails closed.
        Assert.Null(await Engine.Door(fixture.Db, TestSettings.Use())(credential, default));
        Assert.Equal("app", (await Engine.Door(fixture.Db, TestSettings.Use())(shared, default))!.Role);
        Assert.NotNull(await Engine.Door(fixture.Db, new RhythmConfig { App = new() { Viewers = "on" } })(credential, default));
        Assert.Null(await fixture.Db.Authenticate(credential, default));

        // Past its lifetime the door refuses it, and the operator's next list or invite disables it and lists it no more.
        // Until then token list shows it marked as refused.
        var name = (string)(await Scoped.Scalar(fixture.Source, "SELECT name FROM principals WHERE token_hash=@h", ("h", Hashing.Hash(credential))))!;
        async Task<string> Tokens()
        {
            var o = new StringWriter();
            Assert.Equal(0, await TokenCommand.Token(new DeployFolder(Path.GetTempPath()), ["list"], o, principals));
            return o.ToString();
        }
        Assert.Contains($"{name}\tapp\talex\t", await Tokens());
        await Scoped.Execute(fixture.Source, "UPDATE principals SET created_at=now()-interval '31 days' WHERE token_hash=@h", ("h", Hashing.Hash(credential)));
        Assert.Null(await fixture.Db.Authenticate(credential, true, default));
        Assert.Equal(true, await Enabled(credential));
        Assert.Contains($"{name}\tapp\talex (lapsed: refused)\t", await Tokens());
        var (open, _) = await sessions.List(default);
        Assert.Equal(false, await Enabled(credential));
        Assert.DoesNotContain(open, s => s.Created < DateTimeOffset.UtcNow - ViewerSessions.Lifetime);
        Assert.DoesNotContain(name, await Tokens());

        // The same app token again (every start) ends nothing; another one ends every session with the old shared one.
        var kept = await Open(app);
        Assert.Equal(app, await principals.SyncApp(shared, default));
        Assert.NotNull(await fixture.Db.Authenticate(kept, true, default));
        var replaced = Principals.NewToken();
        var now = (await principals.SyncApp(replaced, default))!.Value;
        Assert.Null(await fixture.Db.Authenticate(kept, true, default));
        Assert.Null(await fixture.Db.Authenticate(shared, true, default));
        Assert.Equal(false, await Enabled(kept));
        // A session opened with the new token is admitted.
        Assert.NotNull(await fixture.Db.Authenticate(await Open(now), true, default));
    }

    // A session's upload (SB-P1-20; memory.md section 5, Where writes land): with memory scopes on it is its person's own
    // unless it chooses Family, and the household view's lands where the documents adapter puts it, as before (its owner
    // is Sam here, to tell that apart from Family); with scopes off every upload lands there unless it chooses Family, the
    // session's person only recording who saved it. Each answer names the scope it is stored in and keeps the contract's
    // shape, and its scope says who reads it: shared whenever scopes are off. A session's own upload never meets the one
    // the household view puts in the same person's scope (keyed apart), and with scopes on a login that does not read
    // Family can't add to it (403). With app.viewers off the tool is listed, taken and answered exactly as before: no
    // family, no scope.
    [PostgresFact] public async Task A_sessions_upload_is_its_persons_unless_it_chooses_Family_and_the_household_views_lands_as_before()
    {
        var config = TestSettings.Use();
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"));
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var documents = new DocumentsAdapter();
            var registry = new AdapterRegistry([documents]);
            var db = new Database(fixture.Source, registry.Policies(config));
            documents.Configure(new(new AdapterSettings("documents", new Dictionary<string, object?> { ["enabled"] = "true", ["path"] = folder, ["owner"] = "sam" }),
                config, db, archive));
            var sessions = new ViewerSessions(fixture.Source);
            var (app, shared) = await SharedApp();
            var (code, _) = await sessions.Invite("alex", 15, default);
            var credential = Principals.NewToken();
            Assert.Equal("alex", await sessions.Enrol(app, code, credential, default));
            var session = (await fixture.Db.Authenticate(credential, true, default))!.Id;
            var (samCode, _) = await sessions.Invite("sam", 15, default);
            var samCredential = Principals.NewToken();
            Assert.Equal("sam", await sessions.Enrol(app, samCode, samCredential, default));
            var alexClient = await Scoped.Principal(fixture.Source, "upload-alex-client", "harness", "alex");
            var samClient = await Scoped.Principal(fixture.Source, "upload-sam-client", "harness", "sam");
            await using var door = await ViewerDoor.Open(db, null, registry);
            static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            // One upload through the door: the row it saved (subject and stored scope), what it answered, and its id.
            async Task<(string Row, string Scope, string GraphId, Guid Id)> Add(string token, string text, bool? family = null, string name = "DEMO upload.txt")
            {
                object file = family is { } chosen ? new { name, content = Base64(text), family = chosen }
                    : new { name, content = Base64(text) };
                var (status, answer) = await door.Call(token, AppEndpoint.Upload, file);
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.True((bool)answer!["ok"]!);
                Schemas.Valid(Mcp, "app_upload_document.output.schema.json", answer);
                var document = answer["document"]!;
                var id = Guid.Parse((string)document["resourceId"]!);
                var row = (string)(await Scoped.Scalar(fixture.Source, "SELECT subject_id||' '||scope FROM resources WHERE id=@id", ("id", id)))!;
                return (row, (string)document["scope"]!, (string)document["graphId"]!, id);
            }

            await Scoped.Members(fixture.Source, "alex", "sam");
            await Scoped.Mode(fixture.Source, "on");
            try
            {
                // Alex's session: her own unless she chooses Family; each subject keeps its own copy of the same file.
                var own = await Add(credential, "DEMO: Alex's own fictional upload.");
                Assert.Equal(("alex private", "private"), (own.Row, own.Scope));
                Assert.Contains("alex%3Aupload", own.GraphId);
                Assert.Equal(session.ToString(), await Scoped.Scalar(fixture.Source, "SELECT metadata->>'savedBy' FROM resources WHERE id=@id", ("id", own.Id)));
                Assert.Equal("alex private", (await Add(credential, "DEMO: Alex's own fictional upload.", family: false)).Row);
                var family = await Add(credential, "DEMO: Alex's own fictional upload.", family: true);
                Assert.Equal(("household shared", "shared"), (family.Row, family.Scope));
                Assert.NotEqual(own.Id, family.Id);
                // Read as section 2 says: hers by her own client only, Family's by both.
                Assert.True((await Scoped.Readable(db, alexClient, [own.Id, family.Id])).SetEquals([own.Id, family.Id]));
                Assert.Equal([family.Id], await Scoped.Readable(db, samClient, [own.Id, family.Id]));
                // The household view: where the documents adapter puts it (its owner, Sam), as before, unless it chooses Family.
                var house = await Add(shared, "DEMO: the household view's fictional upload.");
                Assert.Equal(("sam private", "private"), (house.Row, house.Scope));
                var chosen = await Add(shared, "DEMO: the household view's fictional upload.", family: true);
                Assert.Equal(("household shared", "shared"), (chosen.Row, chosen.Scope));

                // Sam's session's own file, and the same bytes from the household view, which the owner setting also puts
                // in Sam's scope: two documents, so the household view's answer never says Sam's file is there (it is
                // created, not unchanged) and its name never renames Sam's.
                const string samsFile = "DEMO: Sam's own fictional file.";
                var sams = await Add(samCredential, samsFile, name: "DEMO sams.txt");
                Assert.Equal(("sam private", "private"), (sams.Row, sams.Scope));
                var (_, again) = await door.Call(shared, AppEndpoint.Upload, new { name = "DEMO renamed.txt", content = Base64(samsFile) });
                Assert.Equal("created", (string?)again!["document"]!["status"]);
                Assert.NotEqual(sams.Id.ToString(), (string?)again["document"]!["resourceId"]);
                Assert.Equal("DEMO sams.txt", await Scoped.Scalar(fixture.Source, "SELECT metadata->>'fileName' FROM resources WHERE id=@id", ("id", sams.Id)));
                Assert.Equal("unchanged", (string?)(await door.Call(samCredential, AppEndpoint.Upload, new { name = "DEMO sams.txt", content = Base64(samsFile) })).Content!["document"]!["status"]);

                // A session whose person is not in Family can't add to it: refused before anything is saved or looked up.
                await Scoped.Execute(fixture.Source, "DELETE FROM memberships WHERE person_id='sam'");
                var rows = await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM resources");
                var (refused, _) = await door.Call(samCredential, AppEndpoint.Upload, new { name = "DEMO upload.txt", content = Base64("DEMO: the household view's fictional upload."), family = true });
                Assert.Equal(HttpStatusCode.Forbidden, refused);
                Assert.Equal(rows, await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM resources"));
                Assert.Equal("sam private", (await Add(samCredential, "DEMO: Sam's other fictional file.")).Row);
                await Scoped.Members(fixture.Source, "sam");
            }
            finally { await Scoped.Mode(fixture.Source, "off"); }

            // Scopes off: every upload lands where the adapter puts it unless it chooses Family, a session's included, and
            // everyone in the household reads it, so its answer says shared wherever it is kept.
            var off = await Add(credential, "DEMO: Alex's fictional upload with scopes off.");
            Assert.Equal(("sam private", "shared"), (off.Row, off.Scope));
            var offFamily = await Add(credential, "DEMO: Alex's fictional upload with scopes off.", family: true);
            Assert.Equal(("household shared", "shared"), (offFamily.Row, offFamily.Scope));
            var offHouse = await Add(shared, "DEMO: the household view's fictional upload with scopes off.");
            Assert.Equal(("sam private", "shared"), (offHouse.Row, offHouse.Scope));
            // With scopes off a non-member's Family choice is taken: everyone reads everything then.
            await Scoped.Execute(fixture.Source, "DELETE FROM memberships WHERE person_id='sam'");
            var nonMember = await Add(samCredential, "DEMO: Sam's fictional Family file.", family: true);
            Assert.Equal(("household shared", "shared"), (nonMember.Row, nonMember.Scope));
            await Scoped.Members(fixture.Source, "sam");

            // app.viewers off: listed and answered as before, and family is outside the schema.
            var health = new AppHealth(db, config);
            static JsonNode Upload(object[] tools) => JsonSerializer.SerializeToNode(tools)!.AsArray().Single(t => (string)t!["name"]! == AppEndpoint.Upload)!;
            var before = Upload(await AppEndpoint.Tools(health, app, default, documents, null, viewers: false));
            Assert.Equal(["content", "name"], before["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order());
            Assert.DoesNotContain("family", (string)before["description"]!);
            var on = Upload(await AppEndpoint.Tools(health, app, default, documents, null, viewers: true));
            Assert.Equal(["content", "family", "name"], on["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order());
            async Task<(int Status, JsonNode Content)> Offline(object arguments)
            {
                var context = new DefaultHttpContext();
                context.Request.Method = "POST";
                context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "u", method = "tools/call",
                    @params = new { name = AppEndpoint.Upload, arguments } }));
                context.Response.Body = new MemoryStream();
                await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, health, registry);
                return (context.Response.StatusCode, JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!);
            }
            var text = Base64("DEMO: the household view's fictional upload with viewer sessions off.");
            var (plain, saved) = await Offline(new { name = "DEMO upload.txt", content = text });
            Assert.Equal(200, plain);
            Assert.Equal(["characters", "format", "graphId", "pages", "resourceId", "status", "title"], saved["document"]!.AsObject().Select(p => p.Key).Order());
            Assert.Equal(400, (await Offline(new { name = "DEMO upload.txt", content = text, family = true })).Status);
        }
        finally { Directory.Delete(folder, true); }
    }
}

// With memory scopes on, over the synthetic corpus: a viewer session reads what its person's own client reads, its
// person's private records and Family, never another person's; no session, or the shared app token with a person sent
// beside it, reads the household view (memory.md sections 2 and 3, SB-P1-11's tests).
[Collection(SharedStateCollection.Name)]
public sealed class ViewerSessionScopeTests(ScopeCorpusFixture fixture) : IClassFixture<ScopeCorpusFixture>
{
    static string[] Ids(GraphPage page) => [.. page.nodes.Select(n => n.id).Order(StringComparer.Ordinal)];
    static string[] Owners(GraphPage page) => [.. page.nodes.Select(n => n.resource.owner).Distinct().Order(StringComparer.Ordinal)];

    [PostgresFact] public async Task A_session_reads_its_persons_private_records_and_Family_and_the_shared_token_the_household_view()
    {
        var load = fixture.Load;
        var host = load.Host;
        var sessions = new ViewerSessions(fixture.Source);
        var shared = Principals.NewToken();
        var app = (await new Principals(fixture.Source).SyncApp(shared, default))!.Value;
        var credential = Principals.NewToken();
        var (code, _) = await sessions.Invite("alex", 15, default);
        Assert.Equal("alex", await sessions.Enrol(app, code, credential, default));
        var viewer = (await load.Db.Authenticate(credential, true, default))!;

        // The session reads exactly what Alex's own client reads: Alex's private stars and Family's, none of Sam's.
        var asViewer = await host.Read(viewer.Id, null, null, default);
        Assert.Equal(Ids(await host.Read(fixture.Readers["alex"], null, null, default)), Ids(asViewer));
        Assert.Contains("alex", Owners(asViewer));
        Assert.DoesNotContain("sam", Owners(asViewer));
        // No session: the shared token reads the household view, as a client with no person does.
        var asApp = await host.Read(app, null, null, default);
        Assert.Equal(Ids(await host.Read(fixture.Readers["family"], null, null, default)), Ids(asApp));
        Assert.DoesNotContain("alex", Owners(asApp));
        Assert.DoesNotContain("sam", Owners(asApp));
        Assert.NotEmpty(asApp.nodes);

        // Through the door and the app's own tool, with a person forged beside the shared token, and as the session.
        await using var door = await ViewerDoor.Open(load.Db, host, load.Registry);
        var forged = new[] { ("X-Rhythm-Person", "alex"), ("Cookie", "person=alex"), ("X-Forwarded-User", "alex@example.invalid") };
        var (status, page) = await door.Call(shared, AppEndpoint.Graph, new { }, forged);
        Assert.Equal(HttpStatusCode.OK, status);
        var owners = page!["nodes"]!.AsArray().Select(n => (string)n!["resource"]!["owner"]!).Distinct().ToArray();
        Assert.DoesNotContain("alex", owners);
        Assert.DoesNotContain("sam", owners);
        var (_, mine) = await door.Call(credential, AppEndpoint.Graph, new { });
        var mineOwners = mine!["nodes"]!.AsArray().Select(n => (string)n!["resource"]!["owner"]!).Distinct().ToArray();
        Assert.Contains("alex", mineOwners);
        Assert.DoesNotContain("sam", mineOwners);
        // One of Sam's private stars, asked for by its id, answers Alex's session and the household view as an unknown star does.
        var samsOwn = (await host.Read(fixture.Readers["sam"], null, null, default)).nodes.First(n => n.resource.owner == "sam");
        foreach (var token in new[] { credential, shared })
        {
            var asked = await door.Call(token, AppEndpoint.Graph, new { operation = "get", value = samsOwn.id });
            var unknown = await door.Call(token, AppEndpoint.Graph, new { operation = "get", value = samsOwn.id + "-unknown" });
            Assert.Equal(unknown.Status, asked.Status);
            Assert.DoesNotContain(samsOwn.id, asked.Content?.ToJsonString() ?? "");
        }
    }
}
