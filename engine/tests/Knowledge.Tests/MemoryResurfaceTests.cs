using System.Text.Json;
using Knowledge.Cli;
using Knowledge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// A resurfaced old memory on Home (SB-P5-17, migration 070, Core/MemoryResurface.cs; docs/dev/memory.md sections 5 and 7.2): one
// old memory a day of what the reader reads, the same one all day, never held, trashed, the engine's own or one a person
// dismissed; a dismissed one never comes back; the app's ops behind memory.tools, off by default. Fictional people only: alex,
// sam and the household; every text is DEMO.
static class Resurfacing
{
    public static readonly MemoryCall Mcp = MemoryLogins.Mcp, App = new("app");

    // A day gone by, at noon in UTC: a memory written today is not old on it until a test moves it back.
    public static readonly DateTimeOffset Day = new(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);

    // A memory written long ago: created_at is the database's clock, so a test moves it back by hand.
    public static async Task<Guid> Old(NpgsqlDataSource s, MemoryStore store, Guid by, string text, string? scope = null, string[]? tags = null,
        string? kind = null, int? importance = null, MemoryCall? call = null)
    {
        var id = (await store.Create(by, new(text, tags, scope, kind, importance), call ?? Mcp, default)).Id;
        await Back(s, id);
        return id;
    }

    public static Task Back(NpgsqlDataSource s, Guid id, string age = "400 days") =>
        MemoryRows.Run(s, $"UPDATE memories SET created_at=@at::timestamptz-interval '{age}' WHERE id=@id", ("at", Day), ("id", id));

    // A Family memory as a Second Brain import brings it, 059's definer called as the terminal calls it: written long ago, under
    // the source the import gives upstream's own jobs' rows, upstream's 0 as not rated, the tags in a namespace the engine sets
    // itself kept beside it.
    public static async Task<Guid> Imported(NpgsqlDataSource s, string key, params string[] upstream)
    {
        var origin = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["origin"] = SecondBrainImport.Origin, ["export"] = Guid.NewGuid().ToString(), ["exportedAt"] = "2025-03-01T00:00:00.000Z",
        });
        var record = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["key"] = key, ["content"] = $"DEMO: imported {key}, on the Pellow porch.", ["source"] = "second-brain",
            ["createdAt"] = Day.AddDays(-400).ToString("O"), ["upstream"] = upstream,
        });
        var answer = (string)(await MemoryRows.Scalar(s, "SELECT import_second_brain_memory('alex',true,@origin::jsonb,@record::jsonb)::text",
            ("origin", origin), ("record", record)))!;
        using var parsed = JsonDocument.Parse(answer);
        Assert.Equal("added", parsed.RootElement.GetProperty("outcome").GetString());
        return parsed.RootElement.GetProperty("id").GetGuid();
    }

    // What a dismissal must not move: the memory's revision and date, its history and its versions.
    public static async Task<string> Unmoved(NpgsqlDataSource s, Guid id) => (string)(await MemoryRows.Scalar(s, """
        SELECT m.revision||' '||m.updated_at||' '||(SELECT count(*) FROM memory_events e WHERE e.memory_id=m.id)
          ||' '||(SELECT count(*) FROM memory_versions v WHERE v.memory_id=m.id)
        FROM memories m WHERE m.id=@id
        """, ("id", id)))!;

    public static async Task<Guid?> Pick(MemoryStore store, Guid reader, DateTimeOffset? at = null) =>
        (await store.Resurface(reader, default, at ?? Day)).Pick?.Id;

    // What the reader is picked, a day at a time for the given days from Day on.
    public static async Task<HashSet<Guid>> Picks(MemoryStore store, Guid reader, int days)
    {
        var seen = new HashSet<Guid>();
        for (var d = 0; d < days; d++)
            if (await Pick(store, reader, Day.AddDays(d)) is { } id) seen.Add(id);
        return seen;
    }

    // Every memory so far out of the way, rated too low to be picked, so a test's own are the only ones in play.
    public static Task Clear(NpgsqlDataSource s) => MemoryRows.Run(s, "UPDATE memories SET importance=1");
}

public sealed class MemoryResurfaceDismissTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_dismissed_pick_never_returns()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "resurface-dismiss");
        var store = new MemoryStore(s);
        var mine = new List<Guid>();
        foreach (var n in new[] { "boiler", "gate", "bins", "ladder" })
            mine.Add(await Resurfacing.Old(s, store, who.Alex, $"DEMO: the Pellow {n} needs a look every spring."));

        var picked = (await Resurfacing.Pick(store, who.App))!.Value;
        Assert.Contains(picked, mine);
        var unmoved = await Resurfacing.Unmoved(s, picked);
        var done = await store.DismissResurfaced(who.App, picked, Resurfacing.App, default);
        Assert.Equal((picked, false), (done.Id, done.Unchanged));
        // Dismissing again changes nothing and keeps one row.
        Assert.True((await store.DismissResurfaced(who.App, picked, Resurfacing.App, default)).Unchanged);
        Assert.Equal(1L, await MemoryRows.Scalar(s, "SELECT count(*) FROM memory_resurface_dismissals WHERE memory_id=@id", ("id", picked)));
        // A dismissal changes no memory: no new revision, no version and no line in its history.
        Assert.Equal(unmoved, await Resurfacing.Unmoved(s, picked));

        // The same day picks among the rest, and no day after brings it back.
        var next = (await Resurfacing.Pick(store, who.App))!.Value;
        Assert.NotEqual(picked, next);
        Assert.DoesNotContain(picked, await Resurfacing.Picks(store, who.App, 120));

        // Nor does anything that happens to the memory: a change, the trash and back, an undo.
        await store.Append(who.Alex, picked, "DEMO: and every autumn.", null, Resurfacing.Mcp, default);
        Assert.DoesNotContain(picked, await Resurfacing.Picks(store, who.App, 30));
        await store.Undo(who.Alex, picked, null, null, Resurfacing.Mcp, default);
        Assert.DoesNotContain(picked, await Resurfacing.Picks(store, who.App, 30));
        var trashed = await store.Forget(who.App, picked, null, Resurfacing.App, default);
        await store.Restore(who.App, picked, trashed.Trash.Nonce, Resurfacing.App, default);
        Assert.DoesNotContain(picked, await Resurfacing.Picks(store, who.App, 30));

        // Dismissed one by one, none is left, and it says so: no day picks any of them again.
        foreach (var id in mine.Where(i => i != picked)) await store.DismissResurfaced(who.App, id, Resurfacing.App, default);
        var none = await store.Resurface(who.App, default, Resurfacing.Day);
        Assert.Null(none.Pick);
        Assert.Equal("2025-03-10", none.Day);
        Assert.Empty(await Resurfacing.Picks(store, who.App, 30));

        // Delete forever takes its dismissal with it, and refuses nothing for having one.
        var gone = await store.Forget(who.App, picked, null, Resurfacing.App, default);
        await store.DeleteForever(who.App, picked, gone.Trash.Nonce, picked.ToString(), Resurfacing.App, default);
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM memory_resurface_dismissals WHERE memory_id=@id", ("id", picked)));
    }

    // The card drew it, and by the tap it is in the trash or held (another device, the screening): the dismissal is kept all the
    // same, so a restore or a release does not bring it back. Only a memory no longer there has nothing to keep.
    [PostgresFact] public async Task A_pick_trashed_or_held_since_the_card_drew_it_stays_dismissed()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Resurfacing.Clear(s);
        var who = await MemoryLogins.Make(s, "resurface-since");
        var store = new MemoryStore(s);

        var thrown = await Resurfacing.Old(s, store, who.Alex, "DEMO: the Pellow coal hole is behind the ivy.");
        Assert.Equal(thrown, await Resurfacing.Pick(store, who.App));
        var trashed = await store.Forget(who.App, thrown, null, Resurfacing.App, default);
        Assert.False((await store.DismissResurfaced(who.App, thrown, Resurfacing.App, default)).Unchanged);
        await store.Restore(who.App, thrown, trashed.Trash.Nonce, Resurfacing.App, default);
        Assert.Empty(await Resurfacing.Picks(store, who.App, 30));

        var held = await Resurfacing.Old(s, store, who.Alex, "DEMO: the Pellow coal hole's cover is loose.");
        Assert.Equal(held, await Resurfacing.Pick(store, who.App));
        await MemoryRows.Run(s, "UPDATE memories SET hold='instruction',hold_score=1 WHERE id=@id", ("id", held));
        Assert.False((await store.DismissResurfaced(who.App, held, Resurfacing.App, default)).Unchanged);
        await store.Release(who.App, held, null, Resurfacing.App, default);
        Assert.Null((await store.Get(who.App, held, default))!.Hold);
        Assert.Empty(await Resurfacing.Picks(store, who.App, 30));

        var gone = await Resurfacing.Old(s, store, who.Alex, "DEMO: the Pellow coal hole was filled in.");
        var bin = await store.Forget(who.App, gone, null, Resurfacing.App, default);
        await store.DeleteForever(who.App, gone, bin.Trash.Nonce, gone.ToString(), Resurfacing.App, default);
        Assert.Equal("unknown", await MemoryLogins.Refused(() => store.DismissResurfaced(who.App, gone, Resurfacing.App, default)));
    }

    [PostgresFact] public async Task Only_a_person_dismisses_and_only_what_they_read()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "resurface-who");
        var store = new MemoryStore(s);
        var alexView = await Scoped.Principal(s, "resurface-alex-view", "app", "alex");
        var samView = await Scoped.Principal(s, "resurface-sam-view", "app", "sam");
        var family = await Resurfacing.Old(s, store, who.Alex, "DEMO: the Family's Pellow bins go out on Tuesdays.", "shared");
        var alexes = await Resurfacing.Old(s, store, who.Alex, "DEMO: Alex's own note on the Pellow allotment.");
        long Rows() => (long)MemoryRows.Scalar(s, "SELECT count(*) FROM memory_resurface_dismissals").GetAwaiter().GetResult()!;
        var before = Rows();

        // An AI client's channel never dismisses: the card is a person's.
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => store.DismissResurfaced(who.Alex, family, Resurfacing.Mcp, default)));
        // A login of no reading role never does, and a channel the login does not have is no channel.
        var ingest = await Scoped.Principal(s, "resurface-ingest", "ingest", plugin: "resurface-plugin");
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => store.DismissResurfaced(ingest, family, Resurfacing.App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.DismissResurfaced(who.Alex, family, Resurfacing.App, default)));
        // Sam does not read Alex's private memory, so to him it is unknown, as one that never was.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => store.DismissResurfaced(samView, alexes, Resurfacing.App, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => store.DismissResurfaced(samView, Guid.NewGuid(), Resurfacing.App, default)));
        // Nor does the household view read it.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => store.DismissResurfaced(who.App, alexes, Resurfacing.App, default)));
        // In the trash or held it is no more theirs to dismiss.
        var thrown = await Resurfacing.Old(s, store, who.Alex, "DEMO: Alex threw away this note on the Pellow allotment.");
        await store.Forget(who.Alex, thrown, null, Resurfacing.Mcp, default);
        var kept = await Resurfacing.Old(s, store, who.Alex, $"{Briefs.Held}, on the Pellow allotment.");
        Assert.NotNull((await store.Get(who.Alex, kept, default))!.Hold);
        foreach (var id in new[] { thrown, kept })
            foreach (var reader in new[] { samView, who.App })
                Assert.Equal("unknown", await MemoryLogins.Refused(() => store.DismissResurfaced(reader, id, Resurfacing.App, default)));
        Assert.Equal(before, Rows());

        // Alex retires it for Alex; Sam still has it.
        Assert.False((await store.DismissResurfaced(alexView, family, Resurfacing.App, default)).Unchanged);
        Assert.Equal(alexes, await Resurfacing.Pick(store, alexView));
        Assert.Equal(family, await Resurfacing.Pick(store, samView));
        Assert.Equal(family, await Resurfacing.Pick(store, who.App));
        // The shared app's Home retires it wherever it is read.
        await store.DismissResurfaced(who.App, family, Resurfacing.App, default);
        Assert.Null(await Resurfacing.Pick(store, samView));
        Assert.Null(await Resurfacing.Pick(store, who.App));
        // A Family memory taken back to Alex's private scope and shared again stays dismissed: the memory is the same one.
        Assert.Equal(alexes, await Resurfacing.Pick(store, alexView));
        await store.DismissResurfaced(alexView, alexes, Resurfacing.App, default);
        await store.Share(who.Alex, alexes, "shared", null, Resurfacing.Mcp, default);
        Assert.Null(await Resurfacing.Pick(store, alexView));
        // Sam, who never dismissed it, has it now it is Family's.
        Assert.Equal(alexes, await Resurfacing.Pick(store, samView));

        // With scopes off everyone reads as one household, so a dismissal is everyone's.
        await Scoped.Mode(s, "off");
        Assert.Null(await Resurfacing.Pick(store, samView));
        Assert.Null(await Resurfacing.Pick(store, who.App));
        await Scoped.Mode(s, "on");
    }

    [PostgresFact] public async Task The_table_changes_only_through_its_function()
    {
        var s = fixture.Source;
        Assert.Equal(true, await MemoryRows.Scalar(s, "SELECT 'memory_resurface_dismissals'::regclass=ANY(checked_tables())"));
    }
}

public sealed class MemoryResurfaceDayTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_reload_shows_the_same_one_all_day_and_the_day_moves_it()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "resurface-day");
        var store = new MemoryStore(s);
        var mine = new List<Guid>();
        for (var n = 0; n < 12; n++) mine.Add(await Resurfacing.Old(s, store, who.Alex, $"DEMO: note {n} on the Pellow roof."));

        var today = await Resurfacing.Pick(store, who.App);
        Assert.Contains(today!.Value, mine);
        // Reload after reload, a new store as a new process is, and from a morning to a night in UTC.
        for (var n = 0; n < 3; n++) Assert.Equal(today, await Resurfacing.Pick(new MemoryStore(s), who.App));
        var start = new DateTimeOffset(2025, 3, 10, 0, 0, 1, TimeSpan.Zero);
        Assert.Equal(today, await Resurfacing.Pick(store, who.App, start));
        Assert.Equal(today, await Resurfacing.Pick(store, who.App, start.AddHours(23.9)));
        // Reading writes nothing, which is why it can be read again.
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM memory_resurface_dismissals"));

        // Taking another memory away does not move it: only its own going does.
        var other = mine.First(m => m != today);
        var trashed = await store.Forget(who.App, other, null, Resurfacing.App, default);
        Assert.Equal(today, await Resurfacing.Pick(store, who.App));
        await store.Restore(who.App, other, trashed.Trash.Nonce, Resurfacing.App, default);
        await store.DismissResurfaced(who.App, other, Resurfacing.App, default);
        Assert.Equal(today, await Resurfacing.Pick(store, who.App));

        // Another day may pick another: over a month the picks are not one memory.
        Assert.True((await Resurfacing.Picks(store, who.App, 30)).Count > 1);
    }

    [PostgresFact] public async Task The_day_is_the_installs_and_a_memory_turns_old_at_its_first_moment()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Resurfacing.Clear(s);
        var who = await MemoryLogins.Make(s, "resurface-zone");
        var store = new MemoryStore(s);
        var id = (await store.Create(who.Alex, new("DEMO: the Pellow cellar floods in a wet spring."), Resurfacing.Mcp, default)).Id;
        await MemoryRows.Run(s, "INSERT INTO install_settings(key,value) VALUES('timezone','Europe/London') ON CONFLICT(key) DO UPDATE SET value=EXCLUDED.value");
        try
        {
            // In London's summer 23:30 UTC is already tomorrow.
            var late = new DateTimeOffset(2025, 7, 10, 23, 30, 0, TimeSpan.Zero);
            Assert.Equal("2025-07-11", (await store.Resurface(who.App, default, late)).Day);
            Assert.Equal("2025-07-10", (await store.Resurface(who.App, default, late.AddHours(-1))).Day);

            // Written within that day's 60th day back, it is not old yet at any moment of this day; it is at the next day's first.
            var firstMoment = new DateTimeOffset(2025, 7, 10, 23, 0, 0, TimeSpan.Zero);
            await MemoryRows.Run(s, "UPDATE memories SET created_at=@at::timestamptz-interval '60 days'+interval '1 hour' WHERE id=@id", ("at", firstMoment), ("id", id));
            Assert.Null(await Resurfacing.Pick(store, who.App, firstMoment.AddHours(-23)));
            Assert.Null(await Resurfacing.Pick(store, who.App, firstMoment.AddMinutes(-1)));
            Assert.Null(await Resurfacing.Pick(store, who.App, firstMoment));
            Assert.Null(await Resurfacing.Pick(store, who.App, firstMoment.AddHours(23.9)));
            Assert.Equal(id, await Resurfacing.Pick(store, who.App, firstMoment.AddDays(1)));
        }
        finally
        {
            await MemoryRows.Run(s, "DELETE FROM install_settings WHERE key='timezone'");
            await MemoryRows.Run(s, "UPDATE memories SET created_at=now() WHERE id=@id", ("id", id));
        }
    }
}

public sealed class MemoryResurfaceEligibleTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The one memory eligible, picked; then not, for each reason.
    [PostgresFact] public async Task Only_an_old_memory_worth_reading_again_is_picked()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Resurfacing.Clear(s);
        var who = await MemoryLogins.Make(s, "resurface-eligible");
        var store = new MemoryStore(s);
        var mcp = Resurfacing.Mcp;
        var alex = who.Alex;

        // Too new today, though it turns old in two days.
        var young = (await store.Create(alex, new("DEMO: just written about the Pellow porch."), mcp, default)).Id;
        await Resurfacing.Back(s, young, "59 days");
        Assert.Null(await Resurfacing.Pick(store, who.App));
        Assert.Equal(young, await Resurfacing.Pick(store, who.App, Resurfacing.Day.AddDays(2)));
        await store.Forget(alex, young, null, mcp, default);

        // Each of what is never picked however old.
        var held = await Resurfacing.Old(s, store, alex, $"{Briefs.Held}, about the Pellow porch.");
        Assert.NotNull((await store.Get(alex, held, default))!.Hold);
        var trashed = await Resurfacing.Old(s, store, alex, "DEMO: thrown away, the Pellow porch.");
        await store.Forget(alex, trashed, null, mcp, default);
        var wrong = await Resurfacing.Old(s, store, alex, "DEMO: marked wrong, the Pellow porch.");
        await store.SetStatus(alex, wrong, "deprecated", null, mcp, default);
        var ended = await Resurfacing.Old(s, store, alex, "DEMO: true once, the Pellow porch.");
        await MemoryRows.Run(s, "UPDATE memories SET valid_until=now()-interval '1 day' WHERE id=@id", ("id", ended));
        var flagged = await Resurfacing.Old(s, store, alex, "DEMO: built on something wrong, the Pellow porch.");
        await MemoryRows.Run(s, "INSERT INTO memory_retraction_flags(memory_id,retracted_id) VALUES(@id,@other)", ("id", flagged), ("other", Guid.NewGuid()));
        var system = await Resurfacing.Old(s, store, alex, "DEMO: a digest of the Pellow porch.");
        await MemoryRows.Run(s, "UPDATE memories SET source='system' WHERE id=@id", ("id", system));
        var slotted = await Resurfacing.Old(s, store, alex, "DEMO: in a capsule slot, the Pellow porch.");
        await MemoryRows.Run(s, "UPDATE memories SET capsule='core',capsule_slot='identity' WHERE id=@id", ("id", slotted));
        var standing = await Standing.Set(store, alex, "DEMO: When the Pellow porch comes up, check the paint first.");
        await Resurfacing.Back(s, standing);
        var episode = await Resurfacing.Old(s, store, alex, "DEMO: a hotel stay near the Pellow porch.", kind: "episodic");
        var done = await Resurfacing.Old(s, store, alex, "DEMO: a finished job on the Pellow porch.", tags: ["task:done"]);
        var low = await Resurfacing.Old(s, store, alex, "DEMO: a small thing about the Pellow porch.", importance: 2);
        // What upstream's own jobs wrote, as an import brings it: under the import's source, not rated, and only the tag kept
        // beside it says whose it is. Upstream's filter leaves each out.
        foreach (var job in new[] { "auto-pattern", "auto-insight", "synthesized" })
            await Resurfacing.Imported(s, $"job-{job}", "confidence:0.70", job);
        // An outside source's: a mirror, ever, and a capture until a person confirms it. Each comes with no status or source on
        // the card to say so.
        var file = Path.Combine(Path.GetTempPath(), $"resurface-capture-{Guid.NewGuid():N}");
        Guid feed;
        try { feed = await new Principals(s).CreateCapture($"resurface-{Guid.NewGuid():N}"[..30], null, file, default); }
        finally { File.Delete(file); }
        var captured = (await store.Capture(feed, new("DEMO: captured, about the Pellow porch."), "feed", null, default)).Id;
        await Resurfacing.Back(s, captured);
        Assert.Empty(await Resurfacing.Picks(store, who.App, 15));
        // Once a person has confirmed the capture it is a memory like any other.
        await store.SetStatus(who.App, captured, "canonical", null, Resurfacing.App, default);
        Assert.Equal(captured, Assert.Single(await Resurfacing.Picks(store, who.App, 15)));
        // A mirror is not, even so: its row is written last, since it can't be back-dated or confirmed once it is one, as a mirror's
        // own read-only rule says, and it goes before the other tests' cleaning.
        await MemoryRows.Run(s, "INSERT INTO memory_mirrors(memory_id,connection,external_id) VALUES(@id,'resurface-feed','resurface:1')", ("id", captured));
        Assert.Empty(await Resurfacing.Picks(store, who.App, 15));
        await MemoryRows.Run(s, "DELETE FROM memory_mirrors WHERE memory_id=@id", ("id", captured));
        await store.Forget(who.App, captured, null, Resurfacing.App, default);
        Assert.Empty(await Resurfacing.Picks(store, who.App, 15));

        // Old enough, rated 3 or up or not rated at all, a fact or not classified: each may be.
        var unrated = await Resurfacing.Old(s, store, alex, "DEMO: not rated, the Pellow porch.");
        Assert.Equal(unrated, await Resurfacing.Pick(store, who.App));
        await store.Forget(alex, unrated, null, mcp, default);
        var rated = await Resurfacing.Old(s, store, alex, "DEMO: rated 3, the Pellow porch.", kind: "semantic", importance: 3);
        Assert.Equal(rated, await Resurfacing.Pick(store, who.App));
        await store.Forget(alex, rated, null, mcp, default);
        // A person's imported memory may be, whatever else is kept beside it.
        var imported = await Resurfacing.Imported(s, "note", "confidence:0.70");
        Assert.Equal(imported, await Resurfacing.Pick(store, who.App));
        await store.Forget(alex, imported, null, mcp, default);

        // Its answer: the place, the date and at most a preview of the words.
        var words = "DEMO: a long note on the Pellow porch. " + new string('x', MemoryStore.ResurfacePreview);
        var longOne = await Resurfacing.Old(s, store, alex, words);
        var pick = (await store.Resurface(who.App, default, Resurfacing.Day)).Pick!;
        Assert.Equal((longOne, words[..MemoryStore.ResurfacePreview], true), (pick.Id, pick.Content, pick.Truncated));
        Assert.Equal(Resurfacing.Day.AddDays(-400), pick.CreatedAt);
    }

    [PostgresFact] public async Task A_held_memory_is_never_picked_nor_its_words_said_and_a_release_makes_it_eligible()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Resurfacing.Clear(s);
        var who = await MemoryLogins.Make(s, "resurface-held");
        var store = new MemoryStore(s);
        var held = await Resurfacing.Old(s, store, who.Alex, $"{Briefs.Held}, on the Pellow shed.");
        Assert.NotNull((await store.Get(who.Alex, held, default))!.Hold);
        var services = AppMemoryCalls.Services(MemoryToolSettings.On, new Database(s));
        for (var d = 0; d < 15; d++) Assert.Null((await store.Resurface(who.App, default, Resurfacing.Day.AddDays(d))).Pick);
        var (status, none) = await AppMemoryCalls.Call(services, who.App, AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "resurface" }));
        Assert.Equal(200, status);
        Assert.DoesNotContain("ignore all previous", none!.ToJsonString());
        // Held since it was the pick: it goes, unasked.
        var fine = await Resurfacing.Old(s, store, who.Alex, "DEMO: the Pellow shed's key is under the pot.");
        Assert.Equal(fine, await Resurfacing.Pick(store, who.App));
        await MemoryRows.Run(s, "UPDATE memories SET hold='instruction',hold_score=1 WHERE id=@id", ("id", fine));
        Assert.Null(await Resurfacing.Pick(store, who.App));
        await MemoryRows.Run(s, "UPDATE memories SET hold=NULL,hold_score=NULL WHERE id=@id", ("id", fine));

        // A person's release makes it an ordinary memory.
        await store.Release(who.App, held, null, Resurfacing.App, default);
        Assert.Contains(held, await Resurfacing.Picks(store, who.App, 30));
    }
}

public sealed class MemoryResurfaceScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_reader_is_picked_only_what_it_reads()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "resurface-scope");
        var store = new MemoryStore(s);
        var alexView = await Scoped.Principal(s, "resurface-scope-alex-view", "app", "alex");
        var samView = await Scoped.Principal(s, "resurface-scope-sam-view", "app", "sam");
        var alexes = new List<Guid>();
        for (var n = 0; n < 6; n++) alexes.Add(await Resurfacing.Old(s, store, who.Alex, $"DEMO: Alex's private note {n} on the Pellow barn."));
        var sams = await Resurfacing.Old(s, store, who.Sam, "DEMO: Sam's private note on the Pellow barn.");

        // Sam's only memory, every day, and never one of Alex's; the household view reads nothing private.
        Assert.Equal([sams], await Resurfacing.Picks(store, samView, 30));
        Assert.Empty(await Resurfacing.Picks(store, who.App, 30));
        Assert.DoesNotContain(sams, await Resurfacing.Picks(store, alexView, 30));
        // Family's is read by both people and the household.
        var family = await Resurfacing.Old(s, store, who.Sam, "DEMO: the Family's Pellow barn roof.", "shared");
        Assert.Equal([family], await Resurfacing.Picks(store, who.App, 30));
        Assert.Contains(family, await Resurfacing.Picks(store, samView, 30));
        Assert.Contains(family, await Resurfacing.Picks(store, alexView, 90));
        // Out of Family, a person reads their own alone.
        await MemoryRows.Run(s, "DELETE FROM memberships WHERE person_id='sam'");
        Assert.Equal([sams], await Resurfacing.Picks(store, samView, 30));
        await MemoryLogins.Family(s);
        // A login that reads nothing is picked nothing.
        var ingest = await Scoped.Principal(s, "resurface-scope-ingest", "ingest", plugin: "resurface-scope-plugin");
        Assert.Empty(await Resurfacing.Picks(store, ingest, 5));
        var legacy = await Scoped.Principal(s, "resurface-scope-legacy", "legacy");
        Assert.Empty(await Resurfacing.Picks(store, legacy, 5));

        // With scopes off the household shares everything, as every read.
        await Scoped.Mode(s, "off");
        var all = await Resurfacing.Picks(store, who.App, 200);
        Assert.Contains(sams, all);
        Assert.True(all.Overlaps(alexes));
        await Scoped.Mode(s, "on");
    }
}

public sealed class MemoryResurfaceOpTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_app_reads_its_pick_and_dismisses_it_in_the_contracts_words()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Resurfacing.Clear(s);
        var who = await MemoryLogins.Make(s, "resurface-op");
        var store = new MemoryStore(s);
        var services = AppMemoryCalls.Services(MemoryToolSettings.On, new Database(s));
        async Task<(int, System.Text.Json.Nodes.JsonNode?)> Op(object args) => await AppMemoryCalls.Call(services, who.App, AppMemoryCalls.Body(AppEndpoint.Memories, args));

        // Nothing old yet: an answer, with no pick.
        var (quiet, none) = await Op(new { op = "resurface" });
        Assert.Equal(200, quiet);
        Assert.Null(none!["pick"]);
        Assert.Equal(await MemoryRows.Scalar(s, "SELECT to_char(install_today(),'YYYY-MM-DD')"), (string)none["day"]!);

        var id = (await store.Create(who.Alex, new("DEMO: the Pellow gutters want clearing in November."), Resurfacing.Mcp, default)).Id;
        await MemoryRows.Run(s, "UPDATE memories SET created_at=now()-interval '90 days' WHERE id=@id", ("id", id));
        var (code, first) = await Op(new { op = "resurface" });
        Assert.Equal(200, code);
        Assert.Equal((id.ToString(), 1, "shared", "household", "DEMO: the Pellow gutters want clearing in November.", false),
            ((string)first!["pick"]!["id"]!, (int)first["pick"]!["revision"]!, (string)first["pick"]!["scope"]!, (string)first["pick"]!["owner"]!,
                (string)first["pick"]!["preview"]!, (bool)first["pick"]!["truncated"]!));
        // The same again on a reload.
        Assert.Equal(first.ToJsonString(), (await Op(new { op = "resurface" })).Item2!.ToJsonString());

        var (gone, dismissed) = await Op(new { op = "resurface_dismiss", id });
        Assert.Equal(200, gone);
        Assert.Equal(("resurface_dismiss", id.ToString(), false), ((string)dismissed!["op"]!, (string)dismissed["id"]!, (bool)dismissed["unchanged"]!));
        Assert.Null((await Op(new { op = "resurface" })).Item2!["pick"]);
        Assert.True((bool)(await Op(new { op = "resurface_dismiss", id })).Item2!["unchanged"]!);

        // A memory that never was is a refusal, nothing written.
        var (refusedCode, refused) = await Op(new { op = "resurface_dismiss", id = Guid.NewGuid() });
        Assert.Equal((200, false, "unknown"), (refusedCode, (bool)refused!["ok"]!, (string)refused["code"]!));

        // Exactly its arguments, and no more.
        bool Allowed(object args) => AppMemories.Allowed(AppMemoryCalls.Request(args).GetProperty("params").GetProperty("arguments"));
        Assert.True(Allowed(new { op = "resurface" }));
        Assert.False(Allowed(new { op = "resurface", scope = "shared" }));
        Assert.False(Allowed(new { op = "resurface", at = "2025-01-01T00:00:00Z" }));
        Assert.True(Allowed(new { op = "resurface_dismiss", id }));
        Assert.False(Allowed(new { op = "resurface_dismiss" }));
        Assert.False(Allowed(new { op = "resurface_dismiss", id = "not-a-uuid" }));
        Assert.False(Allowed(new { op = "resurface_dismiss", id, channel = "cli" }));
    }

    // Off: the app's role answers as it did, before anything is read, and no AI client is offered any of it.
    [Fact] public async Task Off_nothing_answers()
    {
        var off = TestSettings.Use();
        foreach (object args in new object[] { new { op = "resurface" }, new { op = "resurface_dismiss", id = Guid.NewGuid() } })
        {
            var (status, _) = await AppMemoryCalls.Call(AppMemoryCalls.Services(off), Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, args));
            Assert.Equal(403, status);
        }
        foreach (var settings in new[] { off, MemoryToolSettings.On })
        {
            var names = HarnessTools.Create(settings, []).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.DoesNotContain(names, n => n.Contains("resurface", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("app_memories", names);
        }
    }

    [Fact] public void The_ops_are_a_read_and_a_write()
    {
        Assert.Contains("resurface", AppMemories.Reads);
        Assert.Contains("resurface_dismiss", AppMemories.Writes);
        Assert.DoesNotContain("resurface", AppMemories.Writes);
    }
}
