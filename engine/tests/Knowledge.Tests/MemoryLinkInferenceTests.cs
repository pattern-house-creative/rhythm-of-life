using System.Globalization;
using Knowledge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// Links on save (SB-P4-03; migration 062, Core/MemoryLinkInference.cs; docs/dev/memory.md section 8; D-015, D-194, D-200,
// D-211): a save links its memory to at most 3 memories of its scope whose best passage is at or above the threshold,
// relates_to, or follows between two episodic memories written within 30 minutes; never across scopes, never to a held,
// trashed or ended memory, never a pair someone dismissed; each link automatic (suggested), labelled so wherever links
// are read, and a person's to unlink, which keeps it out for good; an edit draws them again, the out-of-date ones the
// upkeep marked (056) included, an imported one too (D-200). These run on a stand-in model whose words point where a
// test says, so a score is the test's own; the corpus's own run is MemoryLinkCorpusTests. Fictional people only.

// A stand-in model: each word it knows points along the axes a test gives it, and a text is the sum of its words, unit
// length; a text with no word it knows points along an axis of its own that no word uses. Passages are whole texts.
internal sealed class WordModel : IEmbedder
{
    public const int Unknown = 383;
    readonly Dictionary<string, (int Axis, float Weight)[]> words = new(StringComparer.Ordinal);
    public string Id => "links-word-model";

    // `word` alike the word along `axis` by `score` (the rest of it on an axis of its own), or a word of its own (score 1).
    public WordModel Word(string word, int axis, double score = 1, int rest = 0)
    {
        words[word] = score >= 1 ? [(axis, 1f)] : [(axis, (float)score), (rest, (float)Math.Sqrt(1 - score * score))];
        return this;
    }

    public float[] Embed(string text)
    {
        var v = new float[384];
        foreach (var token in text.ToLowerInvariant().Split([' ', '.', ',', ':', ';', '\n', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            if (words.TryGetValue(token, out var parts)) foreach (var (axis, weight) in parts) v[axis] += weight;
        if (v.All(x => x == 0)) v[Unknown] = 1;
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }
    public IEnumerable<string> Chunks(string content) { yield return content; }
}

static class Inferring
{
    public static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    public static readonly PassageSources Memories = PassageSources.Of(false, false, true, []);

    // A store that links on save with `model` at `threshold`, and one that does not (a test's setup, the terminal's).
    public static MemoryStore Linking(Database db, IEmbedder model, double threshold = 0.78) =>
        new(db.Source) { Inference = new MemoryLinkInference(db, model, threshold) };

    // A memory written without links on save, then given its passages as the index job would.
    public static async Task<Guid> Stored(Database db, IEmbedder model, Guid who, string text, string? scope = null, string? kind = null)
    {
        var id = (await new MemoryStore(db.Source).Create(who, new($"DEMO: {text}", Scope: scope, Kind: kind), Mcp, default)).Id;
        await new SemanticSearch(db, model, Memories).Index(id, default, Memories);
        return id;
    }

    // Every made link at a memory, as stored: the other end, type, verification, lifecycle, confidence, imported.
    public static async Task<List<(Guid Other, string Type, string Verification, string Lifecycle, double? Confidence, bool Imported, Guid Id, Guid From)>> Links(
        NpgsqlDataSource s, Guid memory)
    {
        var found = new List<(Guid, string, string, string, double?, bool, Guid, Guid)>();
        await using var c = await s.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, """
            SELECT CASE WHEN source_id=@m THEN target_id ELSE source_id END,type,verification,lifecycle,confidence,imported,link_id,source_id
            FROM made_links WHERE @m IN (source_id,target_id) ORDER BY confidence DESC NULLS LAST,link_id
            """, ("m", memory));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            found.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetDouble(4), r.GetBoolean(5), r.GetGuid(6), r.GetGuid(7)));
        return found;
    }

    public static async Task<long> Passages(NpgsqlDataSource s, Guid memory) =>
        (long)(await MemoryRows.Scalar(s, "SELECT count(*) FROM resource_chunks WHERE resource_id=@m", ("m", memory)))!;

    // The end relates_to is stored from: the smaller id, as the database orders them.
    public static Guid Low(Guid a, Guid b) => string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? a : b;

    public static string Vector(float[] v) => "[" + string.Join(',', v.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
}

public sealed class MemoryLinkInferenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_save_links_the_three_closest_memories_of_its_scope_and_nothing_else()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "infer-cap");
        var model = new WordModel().Word("zorvex", 1).Word("kelpa", 1, 0.95, 2).Word("kelpb", 1, 0.90, 3).Word("kelpc", 1, 0.85, 4)
            .Word("kelpd", 1, 0.80, 5).Word("kelpe", 1, 0.70, 6).Word("kelpf", 1, 0.99, 7).Word("kelpg", 1, 0.99, 8);
        var a = await Inferring.Stored(db, model, who.Alex, "kelpa the orchard ladder");
        var b = await Inferring.Stored(db, model, who.Alex, "kelpb the orchard gate");
        var c = await Inferring.Stored(db, model, who.Alex, "kelpc the orchard shed");
        var d = await Inferring.Stored(db, model, who.Alex, "kelpd the orchard hedge");
        var e = await Inferring.Stored(db, model, who.Alex, "kelpe the orchard pond");
        // Closer still, but in Sam's own scope and in Family: never compared with Alex's own memory.
        var sams = await Inferring.Stored(db, model, who.Sam, "kelpf the orchard bench");
        var family = await Inferring.Stored(db, model, who.Alex, "kelpg the orchard path", scope: "shared");
        var before = await MemoryLogins.Now(s);

        var saved = await Inferring.Linking(db, model).Create(who.Alex, new("DEMO: zorvex is the orchard's name", Kind: "semantic"), Inferring.Mcp, default);

        // The write answers as it always did: one revision, one audit row, and nothing written to its neighbours.
        Assert.Equal((1, false, false), (saved.Revision, saved.Unchanged, saved.Replayed));
        Assert.Equal(1L, await MemoryRows.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=@m", ("m", saved.Id)));
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM memory_events WHERE at>=@t AND memory_id<>@m", ("t", before), ("m", saved.Id)));
        Assert.Equal(1L, await Inferring.Passages(s, saved.Id));
        // At most three: the closest three at or above 0.78, relates_to, automatic, made by the saving login, the score as
        // their confidence, from the source end's revision and checked now.
        var links = await Inferring.Links(s, saved.Id);
        Assert.Equal([(a, 0.95), (b, 0.90), (c, 0.85)], links.Select(l => (l.Other, Math.Round(l.Confidence!.Value, 2))));
        Assert.All(links, l => Assert.Equal(("relates_to", "suggested", "active", false), (l.Type, l.Verification, l.Lifecycle, l.Imported)));
        Assert.All(links, l => Assert.Equal(Inferring.Low(saved.Id, l.Other), l.From));
        Assert.Equal("3|3|3|0", await MemoryRows.Scalar(s, """
            SELECT count(*) FILTER (WHERE made_by=@who)||'|'||count(*) FILTER (WHERE source_revision='memory:1')||'|'||count(*) FILTER (WHERE checked_at>=@t)
              ||'|'||count(*) FILTER (WHERE evidence<>'') FROM made_links WHERE @m IN (source_id,target_id)
            """, ("who", who.Alex), ("t", before), ("m", saved.Id)));
        foreach (var other in new[] { d, e, sams, family }) Assert.Empty(await Inferring.Links(s, other));
        // Labelled automatic wherever a reader asks: connections serves each as a made link marked suggested, the saver's to
        // unlink.
        var shown = await new MemoryStore(s).Connections(who.Alex, saved.Id, null, 20, default);
        Assert.Equal(new[] { a, b, c }.Order(), shown!.Items.Select(i => i.Memory.Id).Order());
        Assert.All(shown.Items, i => Assert.Equal((true, "suggested", "none", true, false), (i.Made, i.Verification, i.Direction, i.MayUnlink, i.Imported)));
        // Saving it again under the same key answers the first save and draws nothing new.
        var keyed = await Inferring.Linking(db, model).Create(who.Alex, new("DEMO: zorvex again"), MemoryLogins.Key("infer-cap-key-01"), default);
        Assert.Equal(3, (await Inferring.Links(s, keyed.Id)).Count);
        var again = await Inferring.Linking(db, model).Create(who.Alex, new("DEMO: zorvex again"), MemoryLogins.Key("infer-cap-key-01"), default);
        Assert.True(again.Replayed);
        Assert.Equal(3, (await Inferring.Links(s, keyed.Id)).Count);
    }

    [PostgresFact] public async Task Never_to_a_trashed_ended_or_held_memory_nor_a_pair_a_person_dismissed()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "infer-guard");
        var (alexAtTerminal, _) = await MemoryLogins.Terminals(s);
        var model = new WordModel().Word("marnel", 10).Word("marnela", 10, 0.95, 11).Word("marnelb", 10, 0.93, 12)
            .Word("marnelc", 10, 0.91, 13).Word("marneld", 10, 0.89, 14).Word("marnele", 10, 0.87, 15);
        var trashed = await Inferring.Stored(db, model, who.Alex, "marnela the stall's awning", "shared");
        var held = await Inferring.Stored(db, model, who.Alex, "marnelb the stall's float", "shared");
        var dismissed = await Inferring.Stored(db, model, who.Sam, "marnelc the stall's table", "shared");
        var ended = await Inferring.Stored(db, model, who.Sam, "marneld the stall's old sign", "shared");
        var kept = await Inferring.Stored(db, model, who.Sam, "marnele the stall's rota", "shared");
        var store = new MemoryStore(s);
        await store.Forget(who.Alex, trashed, null, Inferring.Mcp, default);
        await store.SetValidity(who.Sam, ended, new(true, DateTimeOffset.UtcNow.AddDays(-2), true, DateTimeOffset.UtcNow.AddMinutes(-5)), null, Inferring.Mcp, default);
        // A held memory has no passages (026 drops them with the hold); one put back by hand, as a stale index might hold
        // one, is still never linked.
        await MemoryRows.Run(s, "UPDATE memories SET hold='instruction',hold_score=0.9 WHERE id=@m", ("m", held));
        Assert.Equal(0L, await Inferring.Passages(s, held));
        await MemoryRows.Run(s, """
            INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding)
            SELECT m.id,0,'memory:'||m.text_revision,'demo',@model,'DEMO',@v::vector FROM memories m WHERE m.id=@m
            """, ("m", held), ("model", model.Id), ("v", Inferring.Vector(model.Embed("marnelb"))));
        Assert.Equal(1L, await Inferring.Passages(s, held));

        var linking = Inferring.Linking(db, model);
        var saved = await linking.Create(who.Alex, new("DEMO: marnel is the street fair stall", Scope: "shared"), Inferring.Mcp, default);
        Assert.Equal([dismissed, kept], (await Inferring.Links(s, saved.Id)).Select(l => l.Other));

        // A person unlinks one at the terminal: removed, and kept as a dismissal of its type.
        var unlinked = await store.Unlink(alexAtTerminal, saved.Id, dismissed, null, new("cli"), default);
        Assert.Equal(["relates_to"], unlinked.Kept.Select(k => k.Type));
        Assert.Equal([kept], (await Inferring.Links(s, saved.Id)).Select(l => l.Other));
        // An edit draws the links again: the dismissed pair never, the trashed, ended and held ones never.
        await linking.Append(who.Alex, saved.Id, "DEMO: marnel opens at ten", null, Inferring.Mcp, default);
        Assert.Equal([(kept, "active")], (await Inferring.Links(s, saved.Id)).Select(l => (l.Other, l.Lifecycle)));
        // Back from the trash the memory is linked by the next save that finds it once the index job has given it its
        // passages again (a trip to the trash drops them, 005), never by its trip back.
        await store.Restore(who.Alex, trashed, null, Inferring.Mcp, default);
        Assert.Empty(await Inferring.Links(s, trashed));
        await new SemanticSearch(db, model, Inferring.Memories).Index(trashed, default, Inferring.Memories);
        await linking.Update(who.Alex, saved.Id, new(Content: "DEMO: marnel is the street fair stall by the gate"), null, Inferring.Mcp, default);
        Assert.Equal([trashed, kept], (await Inferring.Links(s, saved.Id)).Where(l => l.Lifecycle == "active").Select(l => l.Other));
    }

    [PostgresFact] public async Task A_held_save_is_linked_only_once_a_person_releases_it()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-held");
        var (_, samAtTerminal) = await MemoryLogins.Terminals(s);
        var model = new WordModel().Word("hemlo", 40).Word("hemloa", 40, 0.9, 41);
        var near = await Inferring.Stored(db, model, who.Sam, "hemloa the boathouse booking");
        var planted = Knowledge.Tests.Parity.ParityCorpus.Load().Memories.Single(m => m.Id == "mem-sam-injection-deposit").Text;
        var linking = Inferring.Linking(db, model);
        var saved = await linking.Create(who.Sam, new($"{planted} hemlo"), Inferring.Mcp, default);
        Assert.NotNull(saved.Hold);
        Assert.Empty(await Inferring.Links(s, saved.Id));
        Assert.Equal(0L, await Inferring.Passages(s, saved.Id));
        // A person releases it: its words count from then, and it is linked. This is the store's own step through the
        // terminal's channel on a store that links, which the terminal's store never is (it loads no model); the app's
        // path, the one production takes, is MemoryLinkCorpusTests.Through_the_app_with_scopes_off_a_capture_and_a_release_are_linked.
        var released = await linking.Release(samAtTerminal, saved.Id, null, new("cli"), default);
        Assert.Null(released.Hold);
        Assert.Equal([(near, "relates_to", "suggested")], (await Inferring.Links(s, saved.Id)).Select(l => (l.Other, l.Type, l.Verification)));
        Assert.Equal(1L, await Inferring.Passages(s, saved.Id));
    }

    [PostgresFact] public async Task Follows_joins_two_episodic_memories_only_when_one_came_within_half_an_hour_before()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-follows");
        var model = new WordModel().Word("tessly", 20).Word("tesslya", 20, 0.9, 21).Word("tesslyb", 20, 0.85, 22)
            .Word("brevik", 25).Word("brevika", 25, 0.9, 26).Word("brevikb", 25, 0.85, 27).Word("orlan", 30).Word("orlana", 30, 0.9, 31);
        var linking = Inferring.Linking(db, model);
        // One episodic memory just before, one semantic: follows from the new one to the episodic, relates_to the other.
        var earlier = await Inferring.Stored(db, model, who.Alex, "tesslya the ferry left at nine", kind: "episodic");
        var fact = await Inferring.Stored(db, model, who.Alex, "tesslyb the ferry runs hourly", kind: "semantic");
        var saved = await linking.Create(who.Alex, new("DEMO: tessly the ferry docked at ten", Kind: "episodic"), Inferring.Mcp, default);
        var links = await Inferring.Links(s, saved.Id);
        Assert.Equal([(earlier, "follows", saved.Id), (fact, "relates_to", Inferring.Low(saved.Id, fact))], links.Select(l => (l.Other, l.Type, l.From)));
        // Two episodic memories within the window: which one it follows is not known, so both relate.
        var one = await Inferring.Stored(db, model, who.Alex, "brevika the parcel left the depot", kind: "episodic");
        var two = await Inferring.Stored(db, model, who.Alex, "brevikb the parcel reached the hub", kind: "episodic");
        var parcel = await linking.Create(who.Alex, new("DEMO: brevik the parcel arrived", Kind: "episodic"), Inferring.Mcp, default);
        Assert.Equal([("relates_to", one), ("relates_to", two)], (await Inferring.Links(s, parcel.Id)).Select(l => (l.Type, l.Other)));
        // One written more than half an hour before: relates_to.
        var old = await Inferring.Stored(db, model, who.Alex, "orlana the swim started", kind: "episodic");
        await MemoryRows.Run(s, "UPDATE memories SET created_at=created_at-interval '31 minutes' WHERE id=@m", ("m", old));
        var swim = await linking.Create(who.Alex, new("DEMO: orlan the swim ended", Kind: "episodic"), Inferring.Mcp, default);
        Assert.Equal([("relates_to", old)], (await Inferring.Links(s, swim.Id)).Select(l => (l.Type, l.Other)));
    }

    [PostgresFact] public async Task An_edit_draws_the_links_again_the_upkeep_left_out_of_date_an_imported_one_too()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-edit");
        var model = new WordModel().Word("quorra", 50).Word("quorrap", 50, 0.95, 51).Word("quorraq", 50, 0.80, 52)
            .Word("vintal", 53).Word("vintalr", 53, 0.95, 54).Word("vintalx", 53, 0.90, 55);
        var p = await Inferring.Stored(db, model, who.Alex, "quorrap the boat's mooring");
        var q = await Inferring.Stored(db, model, who.Alex, "quorraq the boat's winter cover");
        var r = await Inferring.Stored(db, model, who.Alex, "vintalr the canal licence");
        var x = await Inferring.Stored(db, model, who.Alex, "vintalx the canal's lock keeper");
        var linking = Inferring.Linking(db, model, threshold: 0.6);
        var saved = await linking.Create(who.Alex, new("DEMO: quorra is the boat"), Inferring.Mcp, default);
        var first = await Inferring.Links(s, saved.Id);
        Assert.Equal([p, q], first.Select(l => l.Other));
        // An automatic link an import brought, as a Second Brain export's would come (no maker, imported).
        var imported = await LinkUpkeep.Suggested(s, saved.Id, x, 0.9, days: 30);

        // New words: the upkeep marks every automatic link at it out of date (056), and the step draws again those that
        // still hold, keeping their ids: p renewed, the imported one renewed and still imported, r made; q, now under the
        // threshold, stays out of date for the pruning pass.
        await linking.Append(who.Alex, saved.Id, "DEMO: vintal is the canal it is moored on", null, Inferring.Mcp, default);
        var now = (await Inferring.Links(s, saved.Id)).ToDictionary(l => l.Other);
        Assert.Equal((first.Single(l => l.Other == p).Id, "active"), (now[p].Id, now[p].Lifecycle));
        Assert.Equal((imported, "active", true, "suggested"), (now[x].Id, now[x].Lifecycle, now[x].Imported, now[x].Verification));
        Assert.Equal(("active", false), (now[r].Lifecycle, now[r].Imported));
        Assert.Equal((first.Single(l => l.Other == q).Id, "stale"), (now[q].Id, now[q].Lifecycle));
        Assert.InRange(now[p].Confidence!.Value, 0.6, 0.7);
        Assert.Null(await MemoryRows.Scalar(s, "SELECT made_by FROM made_links WHERE link_id=@l", ("l", imported)));
        // A week on (the out-of-date one aged as if checked 8 days ago), the pruning pass removes only the one no edit drew
        // again.
        await LinkUpkeep.Age(s, now[q].Id, 8);
        await LinkUpkeep.Pass(s);
        Assert.Equal(new[] { p, r, x }.Order(), (await Inferring.Links(s, saved.Id)).Select(l => l.Other).Order());
        // A change of tags draws nothing again and leaves the links as they are.
        var tagged = await linking.Update(who.Alex, saved.Id, new(Tags: ["boat"]), null, Inferring.Mcp, default);
        Assert.Equal(["tags"], tagged.Changed);
        Assert.Equal(3, (await Inferring.Links(s, saved.Id)).Count(l => l.Lifecycle == "active"));
    }

    [PostgresFact] public async Task An_edit_of_a_follows_earlier_end_keeps_it_a_follows()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-follows-edit");
        var model = new WordModel().Word("fenwy", 70).Word("fenwya", 70, 0.9, 71);
        var linking = Inferring.Linking(db, model);
        var earlier = await Inferring.Stored(db, model, who.Alex, "fenwya the train left", kind: "episodic");
        var later = await linking.Create(who.Alex, new("DEMO: fenwy the train arrived", Kind: "episodic"), Inferring.Mcp, default);
        var drawn = Assert.Single(await Inferring.Links(s, later.Id));
        Assert.Equal(("follows", later.Id, "active"), (drawn.Type, drawn.From, drawn.Lifecycle));
        // The earlier memory's words change: the upkeep marks the follows out of date, and its own step draws the same
        // link again from the later memory, not a relates_to beside it.
        await linking.Append(who.Alex, earlier, "DEMO: fenwya on time", null, Inferring.Mcp, default);
        var after = Assert.Single(await Inferring.Links(s, later.Id));
        Assert.Equal((drawn.Id, "follows", later.Id, "active"), (after.Id, after.Type, after.From, after.Lifecycle));
    }

    [PostgresFact] public async Task An_edit_draws_again_every_automatic_link_that_still_holds_beyond_three()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-five");
        var model = new WordModel().Word("huba", 80);
        for (var i = 0; i < 5; i++) model.Word($"hubq{i}", 80, 0.85, 81 + i);
        var linking = Inferring.Linking(db, model);
        var hub = await Inferring.Stored(db, model, who.Alex, "huba the allotment");
        // Five saves each link to the hub: it holds five automatic links, more than one save draws.
        for (var i = 0; i < 5; i++)
        {
            var w = await linking.Create(who.Alex, new($"DEMO: hubq{i} plot"), Inferring.Mcp, default);
            await new SemanticSearch(db, model, Inferring.Memories).Index(w.Id, default, Inferring.Memories);
        }
        var before = await Inferring.Links(s, hub);
        Assert.Equal(5, before.Count(l => l.Lifecycle == "active"));
        // The hub's words change: all five still score 0.85, so all five are drawn again, the same links, none made.
        await linking.Append(who.Alex, hub, "DEMO: huba again", null, Inferring.Mcp, default);
        var after = await Inferring.Links(s, hub);
        Assert.Equal(before.Select(l => l.Id).Order(), after.Select(l => l.Id).Order());
        Assert.All(after, l => Assert.Equal("active", l.Lifecycle));
    }

    [PostgresFact] public async Task A_move_out_of_Family_takes_its_automatic_links_with_other_scopes_off()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "infer-share");
        var model = new WordModel().Word("pondra", 90).Word("pondraa", 90, 0.9, 91);
        var linking = Inferring.Linking(db, model);
        var alexs = await Inferring.Stored(db, model, who.Alex, "pondraa the shared rota", "shared");
        var sams = await linking.Create(who.Sam, new("DEMO: pondra the rota for the shed", Scope: "shared"), Inferring.Mcp, default);
        Assert.Single(await Inferring.Links(s, sams.Id));
        // A person-made link of Alex's Family memory to one of Sam's stays, as 048 keeps it.
        var stated = await Inferring.Stored(db, model, who.Sam, "the shed key", "shared");
        await new MemoryStore(s).Link(who.Sam, stated, alexs, "relates_to", null, Inferring.Mcp, default);
        var store = new MemoryStore(s);
        // Sam takes his memory back: its automatic link to Alex's Family memory goes with the move, and Alex may then take
        // his own back, which an automatic link to Sam's private memory would have refused.
        await store.Share(who.Sam, sams.Id, "private", null, Inferring.Mcp, default);
        Assert.Empty(await Inferring.Links(s, sams.Id));
        Assert.Equal([stated], (await Inferring.Links(s, alexs)).Select(l => l.Other));
        await store.Share(who.Sam, stated, "private", null, Inferring.Mcp, default);
        // Now the stated link joins Alex's Family memory to Sam's private one: it still keeps Alex's memory in Family.
        await Assert.ThrowsAsync<MemoryRefused>(() => store.Share(who.Alex, alexs, "private", null, Inferring.Mcp, default));
        await store.Share(who.Sam, stated, "shared", null, Inferring.Mcp, default);
        var moved = await store.Share(who.Alex, alexs, "private", null, Inferring.Mcp, default);
        Assert.Equal("private", moved.Scope);
    }

    // An automatic link kept in a trash row is out of a move's reach: restored after its other end moved to another scope,
    // it would join two scopes and keep the restored memory in Family. It does not come back (counted with the links that
    // did not), the person-made links and the automatic ones still in one scope do, and the take-back is allowed.
    [PostgresFact] public async Task A_restore_brings_back_no_automatic_link_whose_other_end_moved_to_another_scope()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "infer-restore");
        var model = new WordModel().Word("larrow", 110).Word("larrowa", 110, 0.9, 111).Word("larrowb", 110, 0.85, 112);
        var linking = Inferring.Linking(db, model);
        var store = new MemoryStore(s);
        var alexs = await Inferring.Stored(db, model, who.Alex, "larrowa the porch light", "shared");
        var family = await Inferring.Stored(db, model, who.Alex, "larrowb the porch step", "shared");
        var sams = await linking.Create(who.Sam, new("DEMO: larrow the porch rota", Scope: "shared"), Inferring.Mcp, default);
        Assert.Equal([alexs, family], (await Inferring.Links(s, sams.Id)).Select(l => l.Other));
        // And a link Sam states to Alex's Family memory, which a move never removes.
        var stated = await Inferring.Stored(db, model, who.Sam, "the porch key", "shared");
        await store.Link(who.Sam, stated, alexs, "relates_to", null, Inferring.Mcp, default);

        // Sam forgets his memory: its links wait in the trash. Alex takes hers back meanwhile, which no live link stops.
        var gone = await store.Forget(who.Sam, sams.Id, null, Inferring.Mcp, default);
        Assert.Empty(await Inferring.Links(s, sams.Id));
        Assert.Equal("private", (await store.Share(who.Alex, alexs, "private", null, Inferring.Mcp, default)).Scope);

        // Sam restores his: the automatic link to Family's memory comes back, the one to Alex's private memory does not.
        var back = await store.Restore(who.Sam, sams.Id, gone.Trash.Nonce, Inferring.Mcp, default);
        Assert.Equal((1, 1), (back.Links, back.LinksGone));
        Assert.Equal([family], (await Inferring.Links(s, sams.Id)).Select(l => l.Other));
        Assert.Equal([stated], (await Inferring.Links(s, alexs)).Select(l => l.Other));
        Assert.Equal(0L, await MemoryRows.Scalar(s, """
            SELECT count(*) FROM made_links l JOIN memories a ON a.id=l.source_id JOIN memories b ON b.id=l.target_id
            WHERE l.verification='suggested' AND a.subject_id<>b.subject_id
            """));
        // So Sam may take his back to his private scope, which a link to Alex's private memory would have refused.
        Assert.Equal("private", (await store.Share(who.Sam, sams.Id, "private", null, Inferring.Mcp, default)).Scope);
        Assert.Empty(await Inferring.Links(s, sams.Id));
    }

    [PostgresFact] public async Task The_author_of_either_end_removes_an_automatic_follows_and_it_stays_out()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "infer-dismiss");
        var (_, samAtTerminal) = await MemoryLogins.Terminals(s);
        var model = new WordModel().Word("varra", 97).Word("varraa", 97, 0.9, 98);
        var linking = Inferring.Linking(db, model);
        var sams = await Inferring.Stored(db, model, who.Sam, "varraa the bus left", "shared", "episodic");
        var alexs = await linking.Create(who.Alex, new("DEMO: varra the bus arrived", Scope: "shared", Kind: "episodic"), Inferring.Mcp, default);
        var drawn = Assert.Single(await Inferring.Links(s, sams));
        Assert.Equal(("follows", alexs.Id), (drawn.Type, drawn.From));
        // Sam wrote the target end only, and Alex's save made the link; it is the engine's suggestion, so Sam may remove it.
        var store = new MemoryStore(s);
        var shown = await store.Connections(who.Sam, sams, null, 20, default);
        Assert.True(Assert.Single(shown!.Items).MayUnlink);
        var unlinked = await store.Unlink(samAtTerminal, sams, alexs.Id, null, new("cli"), default);
        Assert.Equal(["follows"], unlinked.Kept.Select(k => k.Type));
        Assert.Empty(await Inferring.Links(s, sams));
        // Kept out: Alex's next edit draws nothing between them.
        await linking.Append(who.Alex, alexs.Id, "DEMO: varra on time", null, Inferring.Mcp, default);
        Assert.Empty(await Inferring.Links(s, sams));
        // A link a person made stays the maker's or the source end's to remove, as 052 says.
        var mine = await Inferring.Stored(db, model, who.Alex, "the depot", "shared", "episodic");
        await store.Link(who.Alex, mine, sams, "follows", null, Inferring.Mcp, default);
        Assert.False((await store.Connections(who.Sam, sams, null, 20, default))!.Items.Single(i => i.Memory.Id == mine).MayUnlink);
    }

    [PostgresFact] public async Task A_renewed_link_stored_larger_id_first_names_its_source_ends_revision()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-revision");
        var model = new WordModel().Word("qelt", 95).Word("qelta", 95, 0.95, 96);
        var linking = Inferring.Linking(db, model);
        var x = await Inferring.Stored(db, model, who.Alex, "qelta the kiln");
        var saved = await Inferring.Stored(db, model, who.Alex, "qelt the pottery");
        var high = Inferring.Low(saved, x) == saved ? x : saved;
        var low = high == saved ? x : saved;
        // An imported relates_to stored larger id first; the larger end then moves on two revisions.
        var imported = await LinkUpkeep.Suggested(s, high, low, 0.9, days: 30);
        var plain = new MemoryStore(s);
        await plain.Update(who.Alex, high, new(Tags: ["kiln"]), null, Inferring.Mcp, default);
        await plain.Update(who.Alex, high, new(Tags: ["kiln", "fire"]), null, Inferring.Mcp, default);
        await linking.Append(who.Alex, saved, "DEMO: qelt glaze", null, Inferring.Mcp, default);
        Assert.Equal("active|1", await MemoryRows.Scalar(s, """
            SELECT l.lifecycle||'|'||(l.source_revision='memory:'||(SELECT m.revision FROM memories m WHERE m.id=l.source_id))::int
            FROM made_links l WHERE l.link_id=@l
            """, ("l", imported)));
    }

    [PostgresFact] public async Task A_candidate_whose_words_change_while_the_step_waits_is_not_linked()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-race");
        var model = new WordModel().Word("trevan", 99).Word("trevana", 99, 0.9, 100);
        var candidate = await Inferring.Stored(db, model, who.Alex, "trevana the marina berth");
        var saved = await Inferring.Stored(db, model, who.Alex, "trevan the marina");
        var revision = (int)(await MemoryRows.Scalar(s, "SELECT text_revision FROM memories WHERE id=@m", ("m", saved)))!;
        // Another transaction changes the candidate's words and holds its row until the step, having scored the candidate
        // at its old words, waits on that row.
        await using var other = await s.OpenConnectionAsync();
        await using var tx = await other.BeginTransactionAsync();
        await using (var edit = new Npgsql.NpgsqlCommand("UPDATE memories SET text_revision=text_revision+1 WHERE id=@m", other, tx))
        {
            edit.Parameters.AddWithValue("m", candidate);
            await edit.ExecuteNonQueryAsync();
        }
        var step = MemoryRows.Scalar(s, "SELECT infer_memory_links(@p,@m,@r,@v,@model,0.78,'mcp','DEMO client')::text",
            ("p", who.Alex), ("m", saved), ("r", revision), ("v", Inferring.Vector(model.Embed("trevan"))), ("model", model.Id));
        for (var i = 0; i < 100 && !step.IsCompleted; i++)
        {
            if ((long)(await MemoryRows.Scalar(s, "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%infer_memory_links%'"))! > 0) break;
            await Task.Delay(50);
        }
        Assert.False(step.IsCompleted, "the step should wait on the candidate's row");
        await tx.CommitAsync();
        await step;
        Assert.Empty(await Inferring.Links(s, saved));
    }

    [PostgresFact] public async Task A_step_for_a_memory_that_moved_on_or_a_caller_that_may_not_change_it_writes_nothing()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "infer-moved");
        var model = new WordModel().Word("pelmor", 60).Word("pelmora", 60, 0.95, 61);
        await Inferring.Stored(db, model, who.Alex, "pelmora the attic boxes");
        var saved = await Inferring.Stored(db, model, who.Alex, "pelmor the attic");
        var inference = new MemoryLinkInference(db, model, 0.78);
        // Another person's login may not change Alex's memory: nothing is linked.
        var refused = await inference.Run(who.Sam, saved, Inferring.Mcp, default);
        Assert.True(refused is null || refused.Skipped);
        Assert.Empty(await Inferring.Links(s, saved));
        // The step names the words it read: a revision the memory has moved past is skipped, whatever the vector.
        var json = (string)(await MemoryRows.Scalar(s, "SELECT infer_memory_links(@p,@m,99,@v,@model,0.78,'mcp','DEMO client')::text",
            ("p", who.Alex), ("m", saved), ("v", Inferring.Vector(model.Embed("pelmor"))), ("model", model.Id)))!;
        Assert.Contains("\"skipped\": true", json);
        Assert.Empty(await Inferring.Links(s, saved));
        // A bad threshold or vector is refused as invalid input.
        Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT infer_memory_links(@p,@m,1,'[1,0]',@model,0,'mcp')", ("p", who.Alex), ("m", saved), ("model", model.Id)));
        Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT infer_memory_links(@p,@m,1,'not a vector',@model,0.78,'mcp')", ("p", who.Alex), ("m", saved), ("model", model.Id)));
    }
}

// Which stores link on save: the memory tools' and the app's (MemoryTools.Store), only with memory.tools on, the
// embeddings adapter on with its live model loaded, and adapters.embeddings.sources listing memories; the threshold is
// today's 0.78 unless adapters.embeddings.thresholds is tuned. No database needed.
public sealed class MemoryLinkInferenceSettingsTests
{
    static RhythmConfig Settings(string memory, string embeddings) => RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { " + memory + " }\n"
        + "adapters:\n  embeddings:\n" + string.Concat(embeddings.Split('\n').Select(l => "    " + l + "\n")));
    static IServiceProvider Services(RhythmConfig config, Embeddings? embeddings) =>
        (embeddings is null ? new ServiceCollection() : new ServiceCollection().AddSingleton(embeddings))
            .AddSingleton(config).AddSingleton(new Database(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"))).BuildServiceProvider();

    [Fact] public void Off_no_store_links_on_save()
    {
        var on = Settings("tools: on", "enabled: true\nsources: [memories]");
        foreach (var config in new[] { Settings("tools: off", "enabled: true\nsources: [memories]"), Settings("capsules: on", "enabled: true\nsources: [memories]"),
            Settings("tools: on", "enabled: true\nsources: [documents]"), Settings("tools: on", "enabled: false\nsources: [memories]"), TestSettings.Use() })
            Assert.Null(MemoryLinkInference.For(Services(config, Embeddings.Off), config));
        // On, but no model loaded (none in the services, or one that is off): none either.
        Assert.Null(MemoryLinkInference.For(Services(on, null), on));
        Assert.Null(MemoryLinkInference.For(Services(on, Embeddings.Off), on));
        Assert.Null(MemoryTools.Store(Services(on, Embeddings.Off)).Inference);
        TestSettings.Use();
    }

    [ModelFact("minilm")] public void On_the_threshold_is_todays_unless_tuned()
    {
        using var embeddings = new Embeddings();
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!));
        var today = Settings("tools: on", "enabled: true\nsources: [memories]");
        var tuned = Settings("tools: on", "enabled: true\nsources: [memories]\nthresholds: tuned");
        Assert.Equal((0.78, EmbeddingModels.MiniLmId), (MemoryLinkInference.For(Services(today, embeddings), today)!.Threshold, MemoryLinkInference.For(Services(today, embeddings), today)!.Model));
        Assert.Equal(EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId].LinkOnSave, MemoryLinkInference.For(Services(tuned, embeddings), tuned)!.Threshold);
        Assert.Equal(0.58, MemoryLinkInference.For(Services(tuned, embeddings), tuned)!.Threshold);
        Assert.NotNull(MemoryTools.Store(Services(today, embeddings)).Inference);
        Assert.Null(MemoryTools.Store(Services(Settings("tools: off", "enabled: true\nsources: [memories]"), embeddings)).Inference);
        TestSettings.Use();
    }
}
