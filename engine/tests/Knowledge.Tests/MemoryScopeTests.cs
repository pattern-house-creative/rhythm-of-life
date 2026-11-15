using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Csv;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using ModelContextProtocol.Server;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// Memory scopes (docs/dev/memory.md): the scope tables (SB-P1-02), the one read predicate with scopes off and on
// (SB-P1-03), the map per reader (SB-P1-05), and the authorisation tests on the synthetic parity corpus (SB-P1-10, which grows with each phase 1 item).
// The engine refuses a database that records memory.scopes on until phase 1's exit, so these tests turn the recorded
// mode on in their own throwaway databases, after the engine's start-up step has run. All of it is DEMO data.
internal static class Scoped
{
    public static async Task Mode(NpgsqlDataSource source, string mode) =>
        Assert.Equal(1, await Execute(source, "UPDATE install_settings SET value=@m,updated_at=now() WHERE key='memory.scopes'", ("m", mode)));

    public static async Task<int> Execute(NpgsqlDataSource source, string sql, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, sql, args);
        return await cmd.ExecuteScalarAsync();
    }

    // A principal as rhythm token create will make one (SB-P1-12): the person is set when it is made, never after.
    public static async Task<Guid> Principal(NpgsqlDataSource source, string name, string role, string? person = null, string? plugin = null)
    {
        var id = Guid.NewGuid();
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, "INSERT INTO principals(id,name,token_hash,role,plugin,person_id) VALUES(@id,@n,@h,@r,@p,@person)",
            ("id", id), ("n", name), ("h", Hashing.Hash(Principals.NewToken())), ("r", role), ("p", plugin), ("person", person));
        cmd.Parameters["p"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        cmd.Parameters["person"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    // What the first start with scopes on does (memory.md 6.3): everyone then in people joins Family.
    public static Task Members(NpgsqlDataSource source, params string[] people) =>
        Execute(source, "INSERT INTO memberships(person_id) SELECT unnest(@p) ON CONFLICT DO NOTHING", ("p", people));

    // A catalog row; with `native`, a map snapshot row whose source id (Key.ExternalId) it is.
    public static async Task<Guid> Row(NpgsqlDataSource source, string subject, string provider, string scope, string external,
        string status = "current", string? mode = null, string? sourceFile = null, string? native = null)
    {
        var id = Guid.NewGuid();
        await Execute(source, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,metadata,relationship_snapshot)
            VALUES(@id,@s,@p,@e,'document',@scope,'DEMO · scope fixture',@status,
              jsonb_strip_nulls(jsonb_build_object('retrievalMode',@mode::text,'sourceFileId',@file::text)),
              CASE WHEN @native::text IS NULL THEN NULL ELSE jsonb_build_object('Key',jsonb_build_object('ExternalId',@native::text)) END)
            """, ("id", id), ("s", subject), ("p", provider), ("e", external), ("scope", scope), ("status", status),
            ("mode", mode), ("file", sourceFile), ("native", native));
        return id;
    }

    // The rows of `rows` a principal reads through Database.Visible, the predicate every read path takes.
    public static async Task<HashSet<Guid>> Readable(Database db, Guid principal, IEnumerable<Guid> rows, string? predicate = null)
    {
        var json = await db.Read(principal, $"SELECT r.id FROM resources r WHERE {predicate ?? Database.Visible()} AND r.id=ANY(@ids)", default,
            ("ids", rows.ToArray()));
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();
    }

    // The golden queries that name a private record, of `person` or of anyone, as an answer or as one to hide: where a
    // row, or a statistic, from another person's scope would show. Within the search tools' 200-character bound.
    public static string[] PersonalQueries(ParityCorpus corpus, string? person = null)
    {
        var scopes = corpus.ScopeOf;
        return [.. ParityGolden.Load().Queries
            .Where(q => q.Relevant.Select(r => r.Id).Concat(q.Forbids).Any(id => scopes.TryGetValue(id, out var s) && s.StartsWith("personal:" + person, StringComparison.Ordinal)))
            .Select(q => q.Query).Where(q => q.Length <= 200).Distinct()];
    }

    // The SQL state a refused statement ends with.
    public static async Task<string> Refused(Func<Task> act) => (await Assert.ThrowsAsync<PostgresException>(act)).SqlState;

    // A star's snapshot row as a pass saves it (RelationshipEngine.Save): the record's subject, provider and catalog id,
    // the columns its snapshot names, and its times as the database keeps them (to the microsecond).
    public static async Task<Guid> Star(NpgsqlDataSource source, DiscoveryRecord record)
    {
        static DateTimeOffset Kept(DateTimeOffset at) => new(at.UtcTicks / 10 * 10, TimeSpan.Zero);
        var r = record with { RetrievedAt = Kept(record.RetrievedAt), ModifiedAt = record.ModifiedAt is { } m ? Kept(m) : null };
        var id = Guid.NewGuid();
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,revision,source_updated_at,relationship_snapshot,status,domain)
            VALUES(@id,@s,@p,@e,@k,@scope,@t,@u,@rev,@m,@snapshot::jsonb,@status,'unclassified')
            """, ("id", id), ("s", r.Key.Subject), ("p", r.Key.Provider), ("e", r.Key.CatalogId), ("k", r.Kind), ("scope", r.Scope), ("t", r.Title),
            ("u", r.Url), ("rev", r.Revision), ("m", r.ModifiedAt), ("snapshot", JsonSerializer.Serialize(r)), ("status", r.Status));
        cmd.Parameters["u"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        cmd.Parameters["rev"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        cmd.Parameters["m"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    // The newest snapshot row the engine's own principal holds for a star.
    public static async Task<DiscoveryRecord> Snapshot(Database db, Guid system, string graphId)
    {
        await using var c = await db.Source.OpenConnectionAsync();
        return (await RelationshipEngine.Load(system, c, null, false, default)).Where(s => s.Record.Key.GraphId == graphId)
            .MaxBy(s => s.Record.RetrievedAt)!.Record;
    }

    // The answers about one star that must read as those about a star nobody has, once it is unreadable.
    public static readonly string[] AboutOneStar = ["graph get", "graph neighbors", "graph content", "graph data", "explore star", "feeds"];
    public static string Quoted(string id) => JsonSerializer.Serialize(id);
    // Each answer about `star` in `answers` is the unknown star's, with the star's id in its place.
    public static void AnswersAsUnknown(IReadOnlyDictionary<string, string> answers, string star, string who)
    {
        foreach (var op in AboutOneStar)
            Assert.True(answers[$"{op} {MapAnswers.Unknown}"] == answers[$"{op} {star}"].Replace(Quoted(star), Quoted(MapAnswers.Unknown)),
                $"{who}: {op} {star} does not answer as for an unknown star: {answers[$"{op} {star}"]}");
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class MemoryScopeDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The predicate as it stood before migration 023 (Database.Allowed and Visible): the household shares everything.
    const string Today = """
        r.status='current' AND (r.authorized_until IS NULL OR r.authorized_until>now())
        AND EXISTS(SELECT 1 FROM principals p WHERE p.id=@principal AND p.enabled AND (p.role IN ('system','harness','app')
          OR p.role='legacy' AND EXISTS(SELECT 1 FROM grants g WHERE g.principal_id=p.id
            AND g.subject_id=r.subject_id AND g.provider=r.provider AND g.scope=r.scope)))
        """;

    // Section 2's rows, and the people and principals of every kind. Each test names its own, so they never meet.
    sealed record World(Dictionary<string, Guid> Rows, Dictionary<string, Guid> Principals);
    async Task<World> Build(string tag)
    {
        var s = fixture.Source;
        await Scoped.Execute(s, "INSERT INTO subjects(id,kind) VALUES('jo','person') ON CONFLICT DO NOTHING");
        await Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='jo'");
        await Scoped.Members(s, "alex", "sam");
        var rows = new Dictionary<string, Guid>
        {
            ["alex private"] = await Scoped.Row(s, "alex", "notes", "private", tag + "-a"),
            ["alex shared"] = await Scoped.Row(s, "alex", "notes", "shared", tag + "-b"),
            ["sam private"] = await Scoped.Row(s, "sam", "notes", "private", tag + "-c"),
            ["family"] = await Scoped.Row(s, "household", "notes", "shared", tag + "-d"),
            ["household private"] = await Scoped.Row(s, "household", "second-brain", "private", tag + "-e"),
            ["demo"] = await Scoped.Row(s, "fixture", "demo", "private", tag + "-f"),
            ["former person"] = await Scoped.Row(s, "jo", "notes", "private", tag + "-g"),
            ["alex glucose"] = await Scoped.Row(s, "alex", "cgm", "private", tag + "-h"),
            ["alex deleted"] = await Scoped.Row(s, "alex", "notes", "private", tag + "-i", status: "deleted"),
        };
        var principals = new Dictionary<string, Guid>
        {
            ["system"] = await new Principals(s).System("scope-" + tag, default),
            ["household view"] = await Scoped.Principal(s, tag + "-house", "harness"),
            ["alex"] = await Scoped.Principal(s, tag + "-alex", "harness", "alex"),
            ["sam"] = await Scoped.Principal(s, tag + "-sam", "harness", "sam"),
            ["jo"] = await Scoped.Principal(s, tag + "-jo", "harness", "jo"),
            ["legacy"] = await Scoped.Principal(s, tag + "-legacy", "legacy"),
            ["ingest"] = await Scoped.Principal(s, tag + "-ingest", "ingest", plugin: "cgm"),
            ["revoked alex"] = await Scoped.Principal(s, tag + "-revoked", "harness", "alex"),
            // A viewer session's credential (D-070): an app login bound to its person.
            ["alex viewer"] = await Scoped.Principal(s, tag + "-viewer", "app", "alex"),
        };
        await Scoped.Execute(s, "INSERT INTO grants VALUES(@p,'alex','cgm','private')", ("p", principals["legacy"]));
        await Scoped.Execute(s, "UPDATE principals SET enabled=false WHERE id=@p", ("p", principals["revoked alex"]));
        return new(rows, principals);
    }

    async Task<string[]> Reads(World w, string principal) =>
        [.. (await Scoped.Readable(fixture.Db, w.Principals[principal], w.Rows.Values)).Select(id => w.Rows.Single(r => r.Value == id).Key).Order(StringComparer.Ordinal)];

    [PostgresFact] public async Task The_scope_tables_record_off_and_refuse_what_the_contract_forbids()
    {
        var s = fixture.Source;
        Assert.Equal("off", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Mode(s, "maybe")));
        // A login is bound only to a person, and never to another one later.
        var alex = await Scoped.Principal(s, "schema-alex", "harness", "alex");
        Assert.Equal("23503", await Scoped.Refused(() => Scoped.Principal(s, "schema-house", "harness", "household")));
        Assert.Equal("23503", await Scoped.Refused(() => Scoped.Principal(s, "schema-nobody", "harness", "nobody")));
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Principal(s, "schema-plugin", "ingest", "alex", "cgm")));
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE principals SET person_id='sam' WHERE id=@id", ("id", alex))));
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE principals SET person_id=NULL WHERE id=@id", ("id", alex))));
        // Nor does its token move: not onto another person's principal, and not off this one.
        var samBound = await Scoped.Principal(s, "schema-sam-bound", "harness", "sam");
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE principals SET token_hash='moved' WHERE id=@id", ("id", alex))));
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Execute(s,
            "UPDATE principals SET token_hash=(SELECT token_hash FROM principals WHERE id=@from)||'-x' WHERE id=@id", ("from", alex), ("id", samBound))));
        Assert.Equal(1, await Scoped.Execute(s, "UPDATE principals SET enabled=false WHERE id=@id", ("id", alex)));
        Assert.Equal("alex", await Scoped.Scalar(s, "SELECT person_id FROM principals WHERE id=@id", ("id", alex)));
        // Besides a harness login, only an app login is bound: a viewer session's credential (SB-P1-11, D-070), which the
        // perimeter serves the app's tools. No system, legacy or ingest login names a person.
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Principal(s, "schema-system", "system", "alex")));
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Principal(s, "schema-legacy", "legacy", "alex")));
        // The shared app token's login is the one enabled app login with no person: a viewer credential sits beside it,
        // a second unbound one does not, and a new token in the file ends the bound ones too, since the app's server
        // derived each session's credential from the old one.
        var shared = (await new Principals(s).SyncApp(Principals.NewToken(), default))!.Value;
        var viewer = await Scoped.Principal(s, "schema-viewer-alex", "app", "alex");
        Assert.Equal("23505", await Scoped.Refused(() => Scoped.Principal(s, "schema-app-second", "app")));
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE principals SET person_id=NULL WHERE id=@id", ("id", viewer))));
        var rotated = (await new Principals(s).SyncApp(Principals.NewToken(), default))!.Value;
        Assert.NotEqual(shared, rotated);
        Assert.Equal(false, await Scoped.Scalar(s, "SELECT enabled FROM principals WHERE id=@id", ("id", viewer)));
        Assert.Equal(false, await Scoped.Scalar(s, "SELECT enabled FROM principals WHERE id=@id", ("id", shared)));
        Assert.Equal("alex", await Scoped.Scalar(s, "SELECT person_id FROM principals WHERE id=@id", ("id", viewer)));
        // Family's members are people, each a member or an admin.
        Assert.Equal("23503", await Scoped.Refused(() => Scoped.Members(s, "household")));
        Assert.Equal("23503", await Scoped.Refused(() => Scoped.Members(s, "fixture")));
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Execute(s, "INSERT INTO memberships(person_id,role) VALUES('sam','owner')")));
        // A source share is recorded by its person, or by the operator on the host; never by someone else.
        var sam = await Scoped.Principal(s, "schema-sam", "harness", "sam");
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider,shared_by) VALUES('alex','cgm',@by)", ("by", sam))));
        // A revoked principal shares nothing: the alex principal above was disabled.
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider,shared_by) VALUES('alex','cgm',@by)", ("by", alex))));
        var alexNow = await Scoped.Principal(s, "schema-alex-now", "harness", "alex");
        Assert.Equal(1, await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider,shared_by) VALUES('alex','cgm',@by)", ("by", alexNow)));
        // A share is never rewritten, not even to the operator: revoking is a delete.
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE source_shares SET shared_by=@by WHERE person_id='alex'", ("by", sam))));
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE source_shares SET shared_by=NULL WHERE person_id='alex'")));
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE source_shares SET provider='notes' WHERE person_id='alex'")));
        Assert.Equal(1, await Scoped.Execute(s, "DELETE FROM source_shares WHERE person_id='alex'"));
        Assert.Equal(1, await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider) VALUES('alex','cgm')"));
        Assert.Equal("23503", await Scoped.Refused(() => Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider) VALUES('household','cgm')")));
        Assert.Equal(1, await Scoped.Execute(s, "DELETE FROM source_shares WHERE person_id='alex'"));
        // Only people leave or are suspended.
        Assert.Equal("23514", await Scoped.Refused(() => Scoped.Execute(s, "UPDATE subjects SET status='former' WHERE id='household'")));
    }

    [PostgresFact] public async Task With_scopes_off_the_predicate_is_todays_rule()
    {
        var w = await Build("off");
        foreach (var (name, principal) in w.Principals)
            Assert.True((await Scoped.Readable(fixture.Db, principal, w.Rows.Values, Today)).SetEquals(await Scoped.Readable(fixture.Db, principal, w.Rows.Values)), name);
        // The household shares everything, and a binding changes nothing a principal reads.
        string[] current = ["alex glucose", "alex private", "alex shared", "demo", "family", "former person", "household private", "sam private"];
        foreach (var reader in new[] { "system", "household view", "alex", "sam", "jo", "alex viewer" }) Assert.Equal(current, await Reads(w, reader));
        Assert.Equal(["alex glucose"], await Reads(w, "legacy"));
        Assert.Empty(await Reads(w, "ingest"));
        Assert.Empty(await Reads(w, "revoked alex"));
        // The same rule answers the writer's check (Database.Allowed alone), the map rebuild's included.
        Assert.Equal(true, await Scoped.Scalar(fixture.Source, $"SELECT {Database.Allowed("'nobody-yet'", "'notes'", "'private'")}", ("principal", w.Principals["system"])));
    }

    [PostgresFact] public async Task With_scopes_on_a_rows_subject_decides_who_reads_it()
    {
        var w = await Build("on");
        await Scoped.Mode(fixture.Source, "on");
        try
        {
            string[] current = ["alex glucose", "alex private", "alex shared", "demo", "family", "former person", "household private", "sam private"];
            Assert.Equal(current, await Reads(w, "system"));
            // Section 2: a person's rows are theirs whatever the scope column says; the household's are Family's, its
            // private Second Brain workspace included; DEMO rows are everyone's; a former person's are no one's.
            Assert.Equal(["alex glucose", "alex private", "alex shared", "demo", "family", "household private"], await Reads(w, "alex"));
            Assert.Equal(await Reads(w, "alex"), await Reads(w, "alex viewer"));
            Assert.Equal(["demo", "family", "household private", "sam private"], await Reads(w, "sam"));
            Assert.Equal(["demo", "family", "household private"], await Reads(w, "household view"));
            // A principal bound to someone who left, a revoked one, a legacy and an ingest one read nothing.
            foreach (var reader in new[] { "jo", "revoked alex", "legacy", "ingest" }) Assert.Empty(await Reads(w, reader));
            // A source share puts one provider of one person in Family: the household view and Sam read Alex's glucose,
            // and nothing else of hers.
            await Scoped.Execute(fixture.Source, "INSERT INTO source_shares(person_id,provider) VALUES('alex','cgm')");
            Assert.Equal(["alex glucose", "demo", "family", "household private", "sam private"], await Reads(w, "sam"));
            Assert.Equal(["alex glucose", "demo", "family", "household private"], await Reads(w, "household view"));
            // Leaving Family ends Family reads at once, shared sources included; the person's own rows stay theirs.
            await Scoped.Execute(fixture.Source, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Equal(["demo", "sam private"], await Reads(w, "sam"));
            // A suspended person reads nothing, and their rows are no one's but the engine's.
            await Scoped.Members(fixture.Source, "sam");
            await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='suspended' WHERE id='alex'");
            Assert.Empty(await Reads(w, "alex"));
            Assert.Empty(await Reads(w, "alex viewer"));
            Assert.Equal(["demo", "family", "household private", "sam private"], await Reads(w, "sam"));
            Assert.Equal(current, await Reads(w, "system"));
        }
        finally
        {
            await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='current' WHERE id='alex'");
            await Scoped.Execute(fixture.Source, "DELETE FROM source_shares WHERE person_id='alex'");
            await Scoped.Members(fixture.Source, "sam");
            await Scoped.Mode(fixture.Source, "off");
        }
    }

    [PostgresFact] public async Task Copies_are_read_only_by_readers_of_their_original_too_and_narrow_at_the_next_read()
    {
        var s = fixture.Source;
        var w = await Build("copies");
        const string Saved = "dated-snapshot";
        var rows = new Dictionary<string, Guid>
        {
            // Alex's private Drive file, and the household's copy of it an AI client saved before the switch.
            ["alex drive"] = await Scoped.Row(s, "alex", "drive", "private", "copies-o1", native: "fx-copies-1"),
            ["household copy of alex drive"] = await Scoped.Row(s, "household", "documents", "shared", "fx-copies-1", mode: Saved),
            // A legacy household copy (006) names its file in sourceFileId.
            ["alex note"] = await Scoped.Row(s, "alex", "notes", "private", "copies-o2", native: "fx-copies-2"),
            ["legacy copy of alex note"] = await Scoped.Row(s, "household", "household-documents", "shared", "fx-copies-2",
                mode: "dated-reference-snapshot", sourceFile: "fx-copies-2"),
            // Alex's private copy of a Family file is never wider than either: Alex's.
            ["family drive"] = await Scoped.Row(s, "household", "drive", "shared", "copies-o3", native: "fx-copies-3"),
            ["alex copy of family drive"] = await Scoped.Row(s, "alex", "documents", "private", "fx-copies-3", mode: Saved),
            // An upload is an original too, by its own external id.
            ["alex upload"] = await Scoped.Row(s, "alex", "documents", "private", "fx-copies-4", mode: "archived-original"),
            ["household copy of alex upload"] = await Scoped.Row(s, "household", "documents", "shared", "fx-copies-4", mode: Saved),
            // A copy of nothing the engine holds is read by its own subject; so are two copies of one file.
            ["household copy of nothing held"] = await Scoped.Row(s, "household", "documents", "shared", "fx-copies-5", mode: Saved),
            ["sam copy of nothing held"] = await Scoped.Row(s, "sam", "documents", "private", "fx-copies-5", mode: Saved),
        };
        async Task<string[]> Copies(string reader) =>
            [.. (await Scoped.Readable(fixture.Db, w.Principals[reader], rows.Values)).Select(id => rows.Single(r => r.Value == id).Key).Order(StringComparer.Ordinal)];
        string[] all = [.. rows.Keys.Order(StringComparer.Ordinal)];
        Assert.Equal(all, await Copies("sam"));
        await Scoped.Mode(s, "on");
        try
        {
            Assert.Equal(["family drive", "household copy of nothing held", "sam copy of nothing held"], await Copies("sam"));
            Assert.Equal(["family drive", "household copy of nothing held"], await Copies("household view"));
            Assert.Equal(["alex copy of family drive", "alex drive", "alex note", "alex upload", "family drive", "household copy of alex drive",
                "household copy of alex upload", "household copy of nothing held", "legacy copy of alex note"], await Copies("alex"));
            Assert.Equal(all, await Copies("system"));
            // The source moves the file into Alex's private scope (a pass writes its new row): at their next read Sam
            // and the household view lose both copies of it, Sam's own included, with no rebuild and nothing re-stamped.
            rows["alex drive, moved"] = await Scoped.Row(s, "alex", "drive", "private", "copies-o5", native: "fx-copies-5");
            Assert.Equal(["family drive"], await Copies("sam"));
            Assert.Equal(["family drive"], await Copies("household view"));
            // Alex moves her first file into Family (a later pass writes its newer row; the older Alex row stays): the
            // original's newest observation decides, so the household's copy of it is Sam's again.
            rows["alex drive, now family"] = await Scoped.Row(s, "household", "drive", "shared", "copies-o6", native: "fx-copies-1");
            Assert.Equal(["alex drive, now family", "family drive", "household copy of alex drive"], await Copies("sam"));
            // And deleted at its source after that, it still narrows: its newest row is Alex's again.
            rows["alex drive, deleted"] = await Scoped.Row(s, "alex", "drive", "private", "copies-o7", status: "deleted", native: "fx-copies-1");
            Assert.Equal(["alex drive, now family", "family drive"], await Copies("sam"));
        }
        finally { await Scoped.Mode(s, "off"); }
        string[] after = [.. rows.Keys.Where(k => k != "alex drive, deleted").Order(StringComparer.Ordinal)];
        Assert.Equal(after, await Copies("household view"));
    }

    [PostgresFact] public async Task A_copy_of_a_Family_memory_narrows_at_an_in_place_unshare_whatever_its_star_says()
    {
        var s = fixture.Source;
        var w = await Build("memory-copy");
        // A Family memory Alex wrote, its own row last indexed an hour ago, and the map's star for it, which every pass
        // refreshes, so the star is the newer observation of the memory id. The household saved a copy of the memory.
        var memory = await MemoryRows.Memory(s, "household", "alex", "DEMO: the fictional allotment key is under the blue pot.");
        await Scoped.Execute(s, "UPDATE resources SET indexed_at=now()-interval '1 hour' WHERE id=@id", ("id", memory));
        await Scoped.Execute(s, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,relationship_snapshot)
            VALUES(gen_random_uuid(),'household','memory','relationship-engine:'||jsonb_build_array('memory',@m::text)::text,'memory','shared',
              'DEMO memory star','current',jsonb_build_object('Key',jsonb_build_object('ExternalId',@m::text),'RetrievedAt',now()))
            """, ("m", memory.ToString()));
        var copy = await Scoped.Row(s, "household", "documents", "shared", memory.ToString(), mode: "dated-snapshot");
        async Task<bool> Sees(string reader) => (await Scoped.Readable(fixture.Db, w.Principals[reader], [copy])).Count == 1;
        await Scoped.Mode(s, "on");
        try
        {
            Assert.True(await Sees("sam"));
            Assert.True(await Sees("household view"));
            // Alex un-shares it: its row's subject changes in place and nothing is re-stamped (memory.md 8). At the next
            // read, before any pass, the household's copy is Alex's alone: the memory's own row decides, not the star.
            await Scoped.Execute(s, "UPDATE resources SET subject_id='alex',scope='private' WHERE id=@id", ("id", memory));
            Assert.False(await Sees("sam"));
            Assert.False(await Sees("household view"));
            Assert.True(await Sees("alex"));
            // Shared again, it is Sam's again, still with no pass.
            await Scoped.Execute(s, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", memory));
            Assert.True(await Sees("sam"));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // save_document with scopes on (memory.md section 5, Where writes land; migration 031): a login bound to a person
    // saves only for that person or, as a Family member, the household; another person's scope is refused before anything
    // there is read, so the refusal says neither whether that person holds a copy with that file id nor how recent it is,
    // and nothing is written. A subject left out is the caller's own person, or the household's for a login with no
    // person. With scopes off every harness login saves for anyone, and a subject left out is the household's, as before.
    [PostgresFact] public async Task Save_document_lands_only_for_the_callers_own_person_or_Family()
    {
        var s = fixture.Source;
        var w = await Build("save-doc");
        var store = new DocumentStore(s, Embeddings.Off);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        static DocumentSnapshot Doc(string file, string title, DateTimeOffset modified, string? subject = null) =>
            new($"save-doc-{file}", title, null, $"DEMO: {title}, a fictional page.", modified, Subject: subject);
        async Task<Guid> Save(string who, DocumentSnapshot doc) =>
            JsonDocument.Parse(await store.Save(w.Principals[who], doc, default)).RootElement.GetProperty("resourceId").GetGuid();
        async Task<string> Where(Guid id) => (string)(await Scoped.Scalar(s, "SELECT subject_id||'/'||scope FROM resources WHERE id=@id", ("id", id)))!;
        // Every copy with that file id, in any subject: where it is, its title and modified time, and who saved it.
        async Task<string> Copies(string file) => (string)(await Scoped.Scalar(s, """
            SELECT coalesce(string_agg(subject_id||'/'||scope||' '||title||' '||source_updated_at::text||' '||(metadata->>'savedBy'),'; ' ORDER BY subject_id),'none')
            FROM resources WHERE provider='documents' AND external_id=@e
            """, ("e", $"save-doc-{file}")))!;
        async Task<PostgresException> Refused(string who, DocumentSnapshot doc) =>
            await Assert.ThrowsAsync<PostgresException>(() => store.Save(w.Principals[who], doc, default));

        // Scopes off: as before. Alex's client saves her diary for her; a subject left out is the household's, whoever
        // saves; and any harness login saves for any person, Sam's for Alex included.
        var diary = await Save("alex", Doc("diary", "DEMO Alex's diary", now.AddDays(-1), "alex"));
        Assert.Equal("alex/private", await Where(diary));
        Assert.Equal("household/shared", await Where(await Save("alex", Doc("off-default", "DEMO saved by Alex", now))));
        Assert.Equal("household/shared", await Where(await Save("sam", Doc("off-default-sam", "DEMO saved by Sam", now))));
        Assert.Equal("alex/private", await Where(await Save("sam", Doc("off-for-alex", "DEMO saved for Alex by Sam", now, "alex"))));
        var before = await Copies("diary");

        await Scoped.Mode(s, "on");
        try
        {
            // Sam's client names Alex. An older modified time, which 017 answered 'Older source revision' for Alex's diary;
            // a newer one, which 017 let rewrite it; a new file id, which 017 planted in Alex's scope: each the same
            // refusal, and nothing is written.
            foreach (var doc in new[] { Doc("diary", "DEMO overwritten by Sam", new DateTimeOffset(1971, 1, 1, 0, 0, 0, TimeSpan.Zero), "alex"),
                Doc("diary", "DEMO overwritten by Sam", now, "alex"), Doc("planted", "DEMO planted by Sam", now, "alex") })
            {
                var refused = await Refused("sam", doc);
                Assert.Equal(("42501", DocumentStore.Elsewhere), (refused.SqlState, refused.MessageText));
                Assert.DoesNotContain("Older", refused.MessageText);
            }
            Assert.Equal(before, await Copies("diary"));
            Assert.Equal("none", await Copies("planted"));
            // The household view, with no person, saves for the household only.
            Assert.Equal("42501", (await Refused("household view", Doc("house-for-alex", "DEMO by the household view", now, "alex"))).SqlState);
            Assert.Equal("none", await Copies("house-for-alex"));
            Assert.Equal("household/shared", await Where(await Save("household view", Doc("house-default", "DEMO by the household view", now))));
            // A subject left out is the caller's own person; the household when a Family member names it.
            Assert.Equal("alex/private", await Where(await Save("alex", Doc("on-default", "DEMO Alex's own", now))));
            Assert.Equal("sam/private", await Where(await Save("sam", Doc("on-default-sam", "DEMO Sam's own", now))));
            Assert.Equal("household/shared", await Where(await Save("sam", Doc("on-family", "DEMO for Family by Sam", now, "household"))));
            // Alex refreshes her own diary as before: the same row.
            Assert.Equal(diary, await Save("alex", Doc("diary", "DEMO Alex's diary, again", now, "alex")));
            // A login bound to a person who left saves for no one, its own person included.
            Assert.Equal("42501", (await Refused("jo", Doc("jo-own", "DEMO by Jo's client", now))).SqlState);
            Assert.Equal("none", await Copies("jo-own"));
            // Out of Family, Sam saves for himself only.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Equal("42501", (await Refused("sam", Doc("left-family", "DEMO by Sam out of Family", now, "household"))).SqlState);
            Assert.Equal("none", await Copies("left-family"));
            Assert.Equal("sam/private", await Where(await Save("sam", Doc("left-own", "DEMO Sam's own out of Family", now))));
        }
        finally
        {
            await Scoped.Members(s, "sam");
            await Scoped.Mode(s, "off");
        }
        // Scopes off again: as before.
        Assert.Equal("alex/private", await Where(await Save("sam", Doc("off-again", "DEMO saved for Alex by Sam again", now, "alex"))));
        Assert.Equal("household/shared", await Where(await Save("alex", Doc("off-default-again", "DEMO saved by Alex again", now))));
    }

    [PostgresFact] public async Task The_engine_marks_people_who_left_and_refuses_a_database_that_records_on()
    {
        var s = fixture.Source;
        var principals = new Principals(s);
        await Scoped.Members(s, "alex", "sam");
        var withJo = RhythmConfig.Parse(TestSettings.Yaml.Replace("people:\n", "people:\n  - { id: jo, name: Jo }\n", StringComparison.Ordinal));
        await principals.Install(withJo, default);
        var jo = await Scoped.Principal(s, "install-jo", "harness", "jo");
        await Scoped.Members(s, "jo");
        await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider,shared_by) VALUES('jo','cgm',@by)", ("by", jo));
        Assert.Equal("current", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        // Every enabled token bound to Jo: hers, and any this class's other tests made.
        var joTokens = (int)(long)(await Scoped.Scalar(s, "SELECT count(*) FROM principals WHERE enabled AND person_id='jo'"))!;
        Assert.True(joTokens >= 1);
        // Taken out of people: former, out of Family, their shares ended, and every token bound to her disabled for good;
        // their subject and binding stay. The start reports how many, for its log.
        Assert.Equal(new Principals.Started(1, 1, 1, false, 0, joTokens), await principals.Install(TestSettings.Use(), default));
        Assert.Equal("former", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships WHERE person_id='jo'"));
        Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM source_shares WHERE person_id='jo'"));
        Assert.Equal("jo", await Scoped.Scalar(s, "SELECT person_id FROM principals WHERE id=@id", ("id", jo)));
        Assert.Equal(false, await Scoped.Scalar(s, "SELECT enabled FROM principals WHERE id=@id", ("id", jo)));
        Assert.Equal(2L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships WHERE person_id IN ('alex','sam')"));
        Assert.Equal("alex,sam", await Scoped.Scalar(s, "SELECT value FROM install_settings WHERE key=@k", ("k", Principals.ListedKey)));
        Assert.Equal(new Principals.Started(0, 0, 0, false, 0, 0), await principals.Install(TestSettings.Use(), default));
        // Listed again: current, but Family, the share and a token that works only when someone records them again.
        await principals.Install(withJo, default);
        Assert.Equal("current", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        Assert.Equal(0L, await Scoped.Scalar(s, "SELECT count(*) FROM memberships WHERE person_id='jo'"));
        Assert.Equal(false, await Scoped.Scalar(s, "SELECT enabled FROM principals WHERE id=@id", ("id", jo)));
        Assert.Equal(new Principals.Started(1, 0, 0, false, 0, 0), await principals.Install(TestSettings.Use(), default));
        // A suspension is the operator's, not rhythm.yaml's: Jo suspended, taken out of people and listed again is still
        // suspended, and her membership and share end when she is taken out all the same.
        // Her old token stays disabled, so she shares through a new one.
        await principals.Install(withJo, default);
        await Scoped.Members(s, "jo");
        var again = await Scoped.Principal(s, "install-jo-again", "harness", "jo");
        await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider,shared_by) VALUES('jo','cgm',@by)", ("by", again));
        // Suspended by hand, so that new token is still enabled until the start that finds her gone.
        await Scoped.Execute(s, "UPDATE subjects SET status='suspended' WHERE id='jo'");
        Assert.Equal(new Principals.Started(0, 1, 1, false, 0, 1), await principals.Install(TestSettings.Use(), default));
        Assert.Equal("suspended", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        Assert.Equal(new Principals.Started(0, 0, 0, false, 0, 0), await principals.Install(withJo, default));
        Assert.Equal("suspended", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        Assert.Equal(new Principals.Started(0, 0, 0, false, 0, 0), await principals.Install(TestSettings.Use(), default));
        Assert.Equal("suspended", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        // Lifted while she is not listed: she is former at once (Unsuspend), never current; and a status set current by
        // hand is former at the next start, as anyone taken out is.
        Assert.Equal("former", await principals.Unsuspend("jo", default));
        Assert.Equal("former", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        await Scoped.Execute(s, "UPDATE subjects SET status='current' WHERE id='jo'");
        Assert.Equal(new Principals.Started(1, 0, 0, false, 0, 0), await principals.Install(TestSettings.Use(), default));
        Assert.Equal("former", await Scoped.Scalar(s, "SELECT status FROM subjects WHERE id='jo'"));
        // This build runs with scopes off only: a database that records on stops the start, and names the setting.
        await Scoped.Mode(s, "on");
        try { Assert.Contains("memory.scopes", (await Assert.ThrowsAsync<ConfigException>(() => principals.Install(TestSettings.Use(), default))).Message); }
        finally { await Scoped.Mode(s, "off"); }
        await principals.Install(TestSettings.Use(), default);
        // A database that records no mode is refused too, and its reads take the missing mode as on, never as off: a
        // client with no person reads the household's rows and DEMO, not Alex's private one.
        var house = await Scoped.Principal(s, "install-house", "harness");
        var alexNote = await Scoped.Row(s, "alex", "notes", "private", "install-missing-a");
        var familyNote = await Scoped.Row(s, "household", "notes", "shared", "install-missing-b");
        Assert.Equal(1, await Scoped.Execute(s, "DELETE FROM install_settings WHERE key='memory.scopes'"));
        try
        {
            Assert.Contains("memory.scopes", (await Assert.ThrowsAsync<ConfigException>(() => principals.Install(TestSettings.Use(), default))).Message);
            Assert.Equal([familyNote], await Scoped.Readable(fixture.Db, house, [alexNote, familyNote]));
        }
        finally { await Scoped.Execute(s, "INSERT INTO install_settings(key,value) VALUES('memory.scopes','off')"); }
        Assert.Equal(2, (await Scoped.Readable(fixture.Db, house, [alexNote, familyNote])).Count);
        await principals.Install(TestSettings.Use(), default);
    }
}

// A source with one record whose subject the test changes between passes, as a Second Brain memory moved into a
// person's workspace is listed under its new owner.
internal sealed class MovingSource : IMemorySource
{
    public const string Provider = "notes-moving";
    public string Subject = RelationshipDiscovery.Household;
    public string Name => Provider;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct) => Task.FromResult(new SourceListing(
        [new DiscoveryRecord(new(Subject, Provider, Provider, "note-moved"), Subject == RelationshipDiscovery.Household ? "shared" : "private",
            "note", "DEMO · a note that moves", null, "r1", null, DateTimeOffset.UtcNow, [])], true));
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
}

// The re-attribution window on two real rebuilds (memory.md section 8): the pass that lists a record under a new subject
// also reports its old key missing, and that notice is stamped with the pass's start, so it is older than the record's
// new row. From that pass on the star is its new owner's alone, and a household save_document copy of the record
// narrows with it, with no further pass. A class of its own: it rebuilds the map of its own database.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryScopeMoveTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_record_moved_between_two_real_passes_is_its_new_owners_from_the_pass_that_sees_it()
    {
        var s = fixture.Source;
        var config = TestSettings.Use();
        var mover = new MovingSource();
        var registry = new AdapterRegistry([mover]);
        var db = new Database(s, registry.Policies(config));
        var graph = await new Principals(s).System(Principals.GraphName, default);
        var alex = await Scoped.Principal(s, "move-alex", "harness", "alex");
        var sam = await Scoped.Principal(s, "move-sam", "harness", "sam");
        await Scoped.Members(s, "alex", "sam");
        var state = Path.Combine(Path.GetTempPath(), "rhythm-move-" + Guid.NewGuid().ToString("N")[..10]);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (_, _) => Task.FromResult(new CatalogInventory([], []))), registry);
        async Task<string[]> Sees(Guid reader) => [.. (await host.Readable(reader, default)).View.Records
            .Where(r => r.Key.Provider == MovingSource.Provider).Select(r => $"{r.Key.Subject}/{r.Status}")];
        // A household save_document copy of the record: its external id is the record's source id.
        var copy = await Scoped.Row(s, "household", "documents", "shared", "note-moved", mode: "dated-snapshot");
        async Task<bool> SamReadsTheCopy() => (await Scoped.Readable(db, sam, [copy])).Count == 1;
        async Task<string[]> Rows() => [.. JsonDocument.Parse(await db.Read(graph, $"""
            SELECT r.subject_id||'/'||r.status AS row FROM resources r WHERE r.provider='{MovingSource.Provider}'
            ORDER BY (r.relationship_snapshot->>'RetrievedAt')::timestamptz
            """, default)).RootElement.EnumerateArray().Select(r => r.GetProperty("row").GetString()!)];
        try
        {
            await Scoped.Mode(s, "on");
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(["household/current"], await Sees(alex));
            Assert.Equal(["household/current"], await Sees(sam));
            Assert.True(await SamReadsTheCopy());

            mover.Subject = "alex";
            Assert.Equal("complete", await host.Refresh(false, default));
            // The old key's notice is older than the new row, though the pass wrote it after listing.
            Assert.Equal(["household/missing", "alex/current"], await Rows());
            // The pass that sees the move: Alex's at once, nobody else's, and the copy narrows with it.
            Assert.Equal(["alex/current"], await Sees(alex));
            Assert.Empty(await Sees(sam));
            Assert.False(await SamReadsTheCopy());
            // And so it stays.
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(["alex/current"], await Sees(alex));
            Assert.Empty(await Sees(sam));
            Assert.False(await SamReadsTheCopy());
            // With scopes off the household shares everything, as before.
            await Scoped.Mode(s, "off");
            Assert.Equal(["alex/current"], await Sees(sam));
            Assert.True(await SamReadsTheCopy());
        }
        finally
        {
            await Scoped.Mode(s, "off");
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }
}

// The parity corpus loaded as copies (ParityLoader), with a client bound to alex, one to sam, one with no person (the
// household view) and the app, Alex and Sam in Family, Jo gone from people, and scopes on.
public sealed class ScopeCorpusFixture : IAsyncLifetime
{
    public ParityLoad Load { get; private set; } = null!;
    public NpgsqlDataSource Source { get; private set; } = null!;
    public Dictionary<string, Guid> Readers { get; } = [];
    public string Name { get; } = "rhythm_scope_" + Guid.NewGuid().ToString("N")[..12];
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_TEST_DB") is not { Length: > 0 } server) return;
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {Name}")) await create.ExecuteNonQueryAsync();
        Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = Name }.ConnectionString);
        Load = await ParityLoader.Load(Source, ParityCorpus.Load(), Environment.GetEnvironmentVariable("RHYTHM_MODEL_DIR"), ParityMode.Copies);
        Readers["alex"] = await Scoped.Principal(Source, "scope-alex", "harness", "alex");
        Readers["sam"] = await Scoped.Principal(Source, "scope-sam", "harness", "sam");
        Readers["family"] = await Scoped.Principal(Source, "scope-household", "harness");
        Readers["app"] = (await new Principals(Source).SyncApp(Principals.NewToken(), default))!.Value;
        Readers["jo"] = await Scoped.Principal(Source, "scope-jo", "harness", "jo");
        Readers["system"] = await new Principals(Source).System(Principals.GraphName, default);
        await Scoped.Members(Source, "alex", "sam");
        // The engine's start with today's settings, in which Jo is no longer listed.
        await new Principals(Source).Install(TestSettings.Use(), default);
        await Scoped.Mode(Source, "on");
    }
    public async Task DisposeAsync()
    {
        if (Source is null) return;
        await Load.DisposeAsync();
        await Source.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    // The viewer a reader stands for in the corpus's own visibility rule (ParityCorpus.CanSee).
    public static string Viewer(string reader) => reader == "app" ? "family" : reader;
    public Retrieval Retrieval => new(Load.Db, new SourceValidation(Load.Registry));
}

// Semantic search needs the pinned MiniLM files (D-041): without them in RHYTHM_MODEL_DIR the test is skipped.
public sealed class ScopeModelFactAttribute : FactAttribute
{
    public ScopeModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
            Skip = "NOT RUN: needs a disposable Postgres with pgvector; run sh engine/test-db.sh.";
        else if (Environment.GetEnvironmentVariable("RHYTHM_MODEL_DIR") is not { Length: > 0 } folder
            || Embeddings.Files.Any(f => !File.Exists(Path.Combine(folder, f.File))))
            Skip = "NOT RUN: needs the pinned MiniLM files in RHYTHM_MODEL_DIR (D-041).";
    }
}

[Collection(SharedStateCollection.Name)]
public sealed partial class MemoryScopeTests(ScopeCorpusFixture fixture, ITestOutputHelper output) : IClassFixture<ScopeCorpusFixture>
{
    static readonly string[] People = ["alex", "sam"];
    static readonly string[] Readers = ["alex", "sam", "family", "app"];
    static readonly DateOnly From = new(2026, 10, 1), Until = new(2026, 10, 31);
    ParityCorpus Corpus => fixture.Load.Corpus;

    // Every harness tool, and where its scope rule is built: here (SB-P1-03), SB-P1-06's derived records in
    // DerivedScopeTests, the screens' (SB-P1-15) in ScreenScopeTests, or the backlog item that still owes it.
    // A new tool fails Every_harness_tool_is_checked_here_or_waits_for_its_item until it is listed.
    static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string>
    {
        ["search_knowledge"] = "SB-P1-03", ["search_resources"] = "SB-P1-03", ["get_resource"] = "SB-P1-03",
        ["get_related_resources"] = "SB-P1-03", ["semantic_search"] = "SB-P1-03", ["query_health_metrics"] = "SB-P1-03",
        ["compare_health_trends"] = "SB-P1-03", ["discover_health_sources"] = "SB-P1-03", ["read_health_source"] = "SB-P1-03",
        ["investigate_health"] = "SB-P1-03", ["evaluate_health_hypothesis"] = "SB-P1-03", ["get_sync_status"] = "SB-P1-03",
        // The map per reader, its counts, budgets and the re-attribution window (SB-P1-05): checked here and in
        // MemoryScopeMapTests. dashboard_data's star feeds read through the same view; its data and log feeds read their
        // rows as the reader, a log checked against its subject (SB-P1-04): checked here.
        ["get_relationship_graph"] = "SB-P1-05", ["explore_graph"] = "SB-P1-05", ["graph_changes"] = "SB-P1-05",
        ["dashboard_data"] = "SB-P1-04", ["save_dashboard"] = "SB-P1-06", ["list_dashboards"] = "SB-P1-06",
        ["delete_dashboard"] = "SB-P1-06", ["dashboard_history"] = "SB-P1-06", ["save_insight"] = "SB-P1-06",
        ["list_insights"] = "SB-P1-06", ["log_insight_run"] = "SB-P1-06", ["list_screens"] = "SB-P1-15",
        ["control_screen"] = "SB-P1-15",
        // Where a copy lands with scopes on (memory.md section 5, migration 031): checked in MemoryScopeDatabaseTests,
        // Save_document_lands_only_for_the_callers_own_person_or_Family; who reads a copy is the copy rule's, checked here.
        ["save_document"] = "SB-P1-09",
        // Combined retrieval with engine.recall on: Recall.Search for the caller, both arms and the hops on the predicate and
        // every ranking statistic the caller's own (SB-P2-08, RecallScopeTests, which reads through the tool too).
        ["recall"] = "SB-P2-08",
        // A prompt capsule with memory.capsules on: one scope the caller reads, its memories through the predicate on each
        // memory's own row (SB-P3-08, CapsuleDatabaseTests: With_scopes_on_a_capsule_is_one_scope_its_reader_reads).
        ["get_prompt_capsule"] = "SB-P3-08",
        // Studio (docs/dev/studio.md): a draft's feeds and layout are read as its owner through DashboardFeeds and DerivedScopes,
        // and begin refuses a screen that does not read all the agent reads (StudioTests:
        // Real_store_prepares_scoped_feeds_validates_and_publishes_controls, Source_revocation_hides_code_and_expiry_fences_updates).
        // The two selection tools read a manifest only after the dashboard store's own read for the caller (SB-P1-06's rule)
        // and send through the screens' store (SB-P1-15's); neither has a scope test of its own yet.
        ["studio_dashboard"] = "Studio", ["discover_dashboard_controls"] = "Studio", ["invoke_dashboard_action"] = "Studio",
    };

    [Fact] public void Every_harness_tool_is_checked_here_or_waits_for_its_item()
    {
        // recall is two methods, one made in the other's place with memory.tools on (as_of, SB-P4-07): one tool.
        var tools = typeof(HarnessTools).GetMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().Distinct().Order().ToArray();
        Assert.Equal(tools, Tools.Keys.Order());
    }

    // Every app tool, an adapter's included, and every /app route (memory.md 7.2 and 7.4), with the backlog item that
    // puts it on the predicate. SB-P1-04's are checked here: the reads that skipped it (7.4) take it now, and each is
    // read as its caller in a test of its own below (glucose, the Garmin summary, the document route, and app_graph's
    // content and data through CatalogReader; its overview, search, neighbors and get are SB-P1-05's, checked above).
    // The insight and dashboard functions are SB-P1-06's, checked in DerivedScopeTests through the stores both tools
    // call; the screens' are SB-P1-15's, checked in ScreenScopeTests.
    // A new tool or route fails Every_app_tool_and_route_waits_for_its_item until it is listed.
    static readonly IReadOnlyDictionary<string, string> AppReads = new Dictionary<string, string>
    {
        [AppEndpoint.Graph] = "SB-P1-04", [AppEndpoint.LatestGlucose] = "SB-P1-04", [AppEndpoint.RecentGlucose] = "SB-P1-04",
        [Knowledge.Adapters.Garmin.GarminTiles.Summary] = "SB-P1-04", [AppFiles.Prefix] = "SB-P1-04",
        [AppEndpoint.Insights] = "SB-P1-06", [AppEndpoint.Feedback] = "SB-P1-06", [AppEndpoint.Dashboards] = "SB-P1-06",
        [AppEndpoint.Screens] = "SB-P1-15",
        // A write that answers with the owner's sync state, so it takes the predicate too (checked with the summary).
        [Knowledge.Adapters.Garmin.GarminTiles.SyncNow] = "SB-P1-04",
        // Writes that return no rows; an upload lands where section 5 says (SB-P1-20, checked in ViewerSessionDatabaseTests).
        [AppEndpoint.Rebuild] = "write", [AppEndpoint.Upload] = "SB-P1-20",
        // States, and with scopes off install-wide counts (passages per model, rows left), never a row's text, title or id;
        // with scopes on no count at all, since one counts every scope (SB-P2-01, checked in ModelStatusDatabaseTests).
        [AppEndpoint.EngineStatus] = "SB-P2-01",
        // Viewer sessions: no rows, only the person a credential speaks for, listed only while app.viewers is on
        // (checked in ViewerSessionDatabaseTests and ViewerSessionScopeTests).
        [AppEndpoint.Viewer] = "SB-P1-11",
        // Sync now: no rows, an adapter's state and times; with scopes on, only the adapters whose owner's rows the caller
        // reads, any other answering as unknown (checked in SyncNowDatabaseTests).
        [AppEndpoint.Sync] = "SB-P6-09",
        // The app's Memories, with memory.tools on: the store's reads through the predicate on each memory's own row, as
        // the MCP memory tools read them; with scopes on the household view's (checked in AppMemoryDatabaseTests).
        [AppEndpoint.Memories] = "SB-P3-12",
        // Studio for the screen an app login is bound to: the session's reads as that login, a source it lost hiding the
        // candidate (StudioTests: Source_revocation_hides_code_and_expiry_fences_updates).
        [AppStudio.Name] = "Studio",
    };

    [Fact] public void Every_app_tool_and_route_waits_for_its_item()
    {
        // The engine names each app tool and route once, as a constant: AppEndpoint's, an adapter's (AppTool), AppFiles'.
        var named = typeof(AppEndpoint).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("app_", StringComparison.Ordinal) || v.StartsWith("/app/", StringComparison.Ordinal)).Distinct().Order().ToArray();
        Assert.Equal(named, AppReads.Keys.Order());
        // An adapter that adds app tools is listed here, so one that names its tools another way fails too.
        var adding = typeof(AppEndpoint).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Knowledge.Adapters.IAdapter).IsAssignableFrom(t)
                && t.GetInterfaceMap(typeof(Knowledge.Adapters.IAdapter)).TargetMethods
                    .Any(m => m.Name.EndsWith(nameof(Knowledge.Adapters.IAdapter.AppTools), StringComparison.Ordinal) && m.DeclaringType == t))
            .Select(t => t.Name).Order().ToArray();
        Assert.Equal(["GarminAdapter"], adding);
    }

    // Every read of the catalog's rows takes the one predicate (memory.md 7.4, SB-P1-04), so none writes a visibility
    // rule of its own. A row's expiry is the mark of one: Database.Visible states it once, and the map's Served states it
    // once more, for a star's newest observation, beside Database.Allowed. Anywhere else, a read that checks a row's
    // status and expiry but not who reads it fails here, with its file and line. The five reads that skipped the
    // predicate, as the contract named them, each take it now, in code rather than in a comment. Its limit (memory.md
    // 7.4): a read that writes no rule at all, or checks only a row's status, and SQL in the migrations, pass it.
    [Fact] public void No_read_of_the_catalog_writes_a_visibility_rule_of_its_own()
    {
        var src = Path.GetFullPath(Path.Combine(Here(), "..", "..", "src"));
        Assert.True(File.Exists(Path.Combine(src, "Core", "Database.cs")), $"the engine's source is not at {src}");
        string Relative(string file) => Path.GetRelativePath(src, file).Replace('\\', '/');
        // The one bookkeeping read allowed its own rule: rhythm scopes off's preview counts every current record by
        // person and provider for the operator, as counts only, and serves no reader (memory.md 6.2, SB-P1-18).
        bool Bookkeeping((string File, int Line, string Text) l) => l.File == "Core/Principals.cs" && l.Text.TrimStart().StartsWith("var current = ", StringComparison.Ordinal);
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Relative(f).Split('/').Any(part => part is "bin" or "obj"))
            .SelectMany(f => File.ReadLines(f).Select((text, i) => (File: Relative(f), Line: i + 1, Text: text)))
            .Where(l => Expiry().IsMatch(l.Text) && !Bookkeeping(l)).ToArray();
        Assert.True(found.Select(l => l.File).Order().SequenceEqual(["Core/Database.cs", "Core/RelationshipEngine.cs"]),
            "a read with a visibility rule of its own: " + string.Join("; ", found.Select(l => $"{l.File}:{l.Line}")));
        Assert.Contains("{current} AND {Database.Allowed(", File.ReadAllText(Path.Combine(src, "Core", "RelationshipEngine.cs")));
        foreach (var file in new[] { "Core/RelationshipCatalog.cs", "Core/Mcp/AppHealth.cs", "Core/DashboardFeeds.cs", "Core/Mcp/AppFiles.cs", "Adapters/Garmin/GarminTiles.cs" })
            Assert.Contains(File.ReadLines(Path.Combine(src, file)), l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("Database.Visible()", StringComparison.Ordinal));
    }
    static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    [System.Text.RegularExpressions.GeneratedRegex(@"authorized_until\s*>\s*now\(\)")] private static partial System.Text.RegularExpressions.Regex Expiry();

    static JsonElement[] Rows(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.Clone())];
    static string Subject(JsonElement row) => row.GetProperty("subject_id").GetString()!;
    // A person's private rows are theirs alone; Family and DEMO rows are every member's and the household view's.
    static bool Reads(string reader, string subject) => subject is "household" or RhythmConfig.Synthetic || subject == ScopeCorpusFixture.Viewer(reader);

    [PostgresFact] public async Task Each_reader_gets_exactly_the_corpus_records_its_scopes_hold()
    {
        var retrieval = fixture.Retrieval;
        foreach (var reader in Readers)
        {
            var viewer = ScopeCorpusFixture.Viewer(reader);
            foreach (var (id, resource) in fixture.Load.ResourceIds)
                Assert.True(Corpus.CanSee(viewer, Corpus.ScopeOf[id]) == (await retrieval.Resource(fixture.Readers[reader], resource, default) != "[]"), $"{reader} {id}");
        }
        // Jo left people: her records are on the map under her subject and read by no one but the engine, and a client
        // still bound to her reads nothing at all.
        var jo = Rows(await fixture.Load.Db.Read(fixture.Readers["system"], $"SELECT r.id FROM resources r WHERE {Database.Visible()} AND r.subject_id='jo'", default))
            .Select(r => r.GetProperty("id").GetGuid()).ToArray();
        Assert.NotEmpty(jo);
        foreach (var reader in Readers) Assert.Empty(await Scoped.Readable(fixture.Load.Db, fixture.Readers[reader], jo));
        Assert.Equal(0L, await Scoped.Scalar(fixture.Source, $"SELECT count(*) FROM resources r WHERE {Database.Visible()}", ("principal", fixture.Readers["jo"])));
    }

    [PostgresFact] public async Task Sam_reads_none_of_Alexs_private_rows_through_any_tool()
    {
        var retrieval = fixture.Retrieval;
        var recall = new Recall(fixture.Load.Db, fixture.Load.Embeddings.Model, fixture.Load.Covered, new SourceValidation(fixture.Load.Registry),
            embeddingsStatus: fixture.Load.Embeddings.Status);
        var health = new HealthInvestigation(fixture.Load.Db);
        await Scoped.Execute(fixture.Source, """
            INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date) VALUES(gen_random_uuid(),'alex','garmin','2026-10-01','2026-10-31','2026-10-31'),
              (gen_random_uuid(),'sam','garmin','2026-10-01','2026-10-31','2026-10-31');
            INSERT INTO ingest_freshness(plugin,provider,subject_id,last_accepted_at) VALUES('cgm','cgm','alex',now()),('cgm','cgm','household',now())
            ON CONFLICT DO NOTHING
            """);
        // Two links that cross scopes, as an engine's similarity link may (memory.md section 5): only a reader of both
        // ends is served one.
        var family = fixture.Load.ResourceIds[Corpus.Memories.First(m => m.Scope == "family").Id];
        var alexMemory = fixture.Load.ResourceIds[Corpus.Memories.First(m => m.Scope == "personal:alex").Id];
        var samMemory = fixture.Load.ResourceIds[Corpus.Memories.First(m => m.Scope == "personal:sam").Id];
        await Scoped.Execute(fixture.Source, """
            INSERT INTO relationships(source_id,target_id,type,verification,evidence) VALUES(@f,@a,'similar','suggested',''),(@s,@f,'similar','suggested','')
            ON CONFLICT DO NOTHING
            """, ("f", family), ("a", alexMemory), ("s", samMemory));
        // Each person, and the app's household view; a harness with no person reads the same, which the test above checks.
        foreach (var reader in new[] { "alex", "sam", "app" })
        {
            var principal = fixture.Readers[reader];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Check(string tool, IEnumerable<JsonElement> rows)
            {
                foreach (var row in rows)
                {
                    Assert.True(Reads(reader, Subject(row)), $"{reader} read a row of {Subject(row)} through {tool}");
                    seen.Add(Subject(row));
                }
            }
            foreach (var q in Scoped.PersonalQueries(Corpus))
            {
                Check("search_knowledge", Rows(await retrieval.Search(principal, q, 50, default)));
                Check("search_resources", Rows(await retrieval.SearchResources(principal, q, "", "", "", "", 50, default)));
                // Combined retrieval (SB-P2-08): both arms (the meaning arm when the model files are local), and links
                // followed three hops from what this reader is served, across the two crossing links above.
                Check("recall", (await recall.Search(principal, new RecallQuery(q, 20, 3), default)).Hits.Select(h => h.Row));
            }
            foreach (var resource in fixture.Load.ResourceIds.Values)
            {
                Check("get_resource", Rows(await retrieval.Resource(principal, resource, default)));
                Check("get_related_resources", Rows(await retrieval.Related(principal, resource, 50, default)));
            }
            using var status = JsonDocument.Parse(await retrieval.Status(principal, default));
            Check("get_sync_status", status.RootElement.GetProperty("jobs").EnumerateArray());
            Check("get_sync_status", status.RootElement.GetProperty("ingest").EnumerateArray());
            foreach (var person in People)
            {
                var allowed = Reads(reader, person);
                Check("search_resources", Rows(await retrieval.SearchResources(principal, "", "", "", "", person, 50, default)));
                foreach (var series in Corpus.Measurements.Where(m => m.Subject == person))
                {
                    var metrics = Rows(await retrieval.Metrics(principal, person, series.Metric, From, Until, 200, default));
                    Assert.Equal(allowed, metrics.Length > 0);
                    Check("query_health_metrics", metrics);
                    Assert.Equal(allowed, Rows(await retrieval.Trends(principal, person, series.Metric, series.Metric, From, Until, default)).Length > 0);
                    using var source = JsonDocument.Parse(await health.ReadSource(principal, fixture.Load.ResourceIds[series.Id], "", 0, 20, false, default));
                    Assert.Equal(allowed, source.RootElement.GetProperty("status").GetString() != "unavailable");
                }
                using var discovered = JsonDocument.Parse(await health.Discover(principal, person, From, Until, "", 0, default));
                Assert.Equal(allowed, discovered.RootElement.GetProperty("status").GetString() == "available");
                Assert.Equal(allowed, discovered.RootElement.GetProperty("resources").GetArrayLength() > 0);
                using var investigated = JsonDocument.Parse(await health.Investigate(principal, person, From, Until, "", 3, default));
                Assert.Equal(allowed, investigated.RootElement.GetProperty("discovery").GetProperty("sources").GetArrayLength() > 0);
                // Without a reading the hypothesis has no series to choose from; with them it has one of each.
                using var hypothesis = JsonDocument.Parse(await health.Hypothesis(principal, person, From, Until, "sleep_minutes", "resting_heart_rate", "daily", 0, 60, 0, default));
                Assert.Equal(allowed, !(hypothesis.RootElement.TryGetProperty("available", out var choices) && choices.GetArrayLength() == 0));
            }
            // Not a pass by finding nothing: each reader is served its own scopes.
            Assert.Contains("household", seen);
            if (ScopeCorpusFixture.Viewer(reader) is var own && own != "family") Assert.Contains(own, seen);
            output.WriteLine($"{reader}: rows of {string.Join(", ", seen.Order())}");
        }
    }

    [ScopeModelFact] public async Task Semantic_search_serves_each_reader_only_its_scopes()
    {
        var model = fixture.Load.Embeddings.Model;
        Assert.NotNull(model);
        var search = new SemanticSearch(fixture.Load.Db, model);
        foreach (var reader in Readers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in Scoped.PersonalQueries(Corpus))
                foreach (var row in Rows(await search.Search(fixture.Readers[reader], q, "", 20, default)))
                {
                    Assert.True(Reads(reader, Subject(row)), $"{reader} read a passage of {Subject(row)}");
                    seen.Add(Subject(row));
                }
            Assert.Contains("household", seen);
            if (ScopeCorpusFixture.Viewer(reader) is var own && own != "family") Assert.Contains(own, seen);
        }
    }

    [PostgresFact] public async Task A_household_copy_of_Alexs_private_Drive_file_saved_before_the_switch_is_not_Sams()
    {
        var retrieval = fixture.Retrieval;
        var original = Corpus.Documents.First(d => d.Scope == "personal:alex");
        const string Word = "Quillowby";
        await Scoped.Mode(fixture.Source, "off");
        try
        {
            // An AI client with no person saved the household a copy of Alex's file, as every copy is saved today.
            var saved = await new DocumentStore(fixture.Source, fixture.Load.Embeddings).Save(fixture.Load.Harness,
                new DocumentSnapshot(ParityLoader.FilePrefix + original.Id, $"{Word} copy of {original.Title}", null, $"{Word}. {original.Text}",
                    original.Source.ModifiedAt, Subject: RhythmConfig.Household), default);
            var copy = JsonDocument.Parse(saved).RootElement.GetProperty("resourceId").GetGuid();
            foreach (var reader in Readers) Assert.NotEqual("[]", await retrieval.Resource(fixture.Readers[reader], copy, default));
            await Scoped.Mode(fixture.Source, "on");
            foreach (var reader in Readers)
            {
                var allowed = ScopeCorpusFixture.Viewer(reader) == "alex";
                Assert.Equal(allowed, await retrieval.Resource(fixture.Readers[reader], copy, default) != "[]");
                Assert.Equal(allowed, Rows(await retrieval.Search(fixture.Readers[reader], Word, 50, default)).Any(r => r.GetProperty("id").GetGuid() == copy));
                Assert.Equal(allowed, Rows(await retrieval.SearchResources(fixture.Readers[reader], Word, "", "", "", "", 50, default)).Any(r => r.GetProperty("id").GetGuid() == copy));
                if (fixture.Load.Embeddings.Model is { } model)
                    Assert.Equal(allowed, Rows(await new SemanticSearch(fixture.Load.Db, model).Search(fixture.Readers[reader], $"{Word} {original.Title}", "", 20, default))
                        .Any(r => r.GetProperty("id").GetGuid() == copy));
            }
        }
        finally { await Scoped.Mode(fixture.Source, "on"); }
    }

    [PostgresFact] public async Task A_revoked_principal_and_one_whose_person_is_suspended_read_nothing()
    {
        var retrieval = fixture.Retrieval;
        var second = await Scoped.Principal(fixture.Source, "scope-sam-second", "harness", "sam");
        var sams = Corpus.Memories.Where(m => m.Scope == "personal:sam").Select(m => fixture.Load.ResourceIds[m.Id]).ToArray();
        Assert.Equal(sams.Length, (await Scoped.Readable(fixture.Load.Db, second, sams)).Count);
        Assert.NotEqual("[]", await retrieval.Search(second, "allotment", 50, default));
        // Revoked: nothing at its next read, through any path.
        Assert.Equal(1, await new Principals(fixture.Source).Revoke("scope-sam-second", default));
        Assert.Empty(await Scoped.Readable(fixture.Load.Db, second, fixture.Load.ResourceIds.Values));
        Assert.Equal("[]", await retrieval.Search(second, "allotment", 50, default));
        Assert.Equal("{\"jobs\":[],\"ingest\":[]}", await retrieval.Status(second, default));
        // Suspended (SB-P1-12): every client bound to Sam reads nothing, and Sam's rows are no one's but the engine's.
        await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='suspended' WHERE id='sam'");
        try
        {
            Assert.Empty(await Scoped.Readable(fixture.Load.Db, fixture.Readers["sam"], fixture.Load.ResourceIds.Values));
            foreach (var reader in new[] { "alex", "family", "app" }) Assert.Empty(await Scoped.Readable(fixture.Load.Db, fixture.Readers[reader], sams));
            Assert.Equal(sams.Length, (await Scoped.Readable(fixture.Load.Db, fixture.Readers["system"], sams)).Count);
        }
        finally { await Scoped.Execute(fixture.Source, "UPDATE subjects SET status='current' WHERE id='sam'"); }
        Assert.Equal(sams.Length, (await Scoped.Readable(fixture.Load.Db, fixture.Readers["sam"], sams)).Count);
    }

    // Story 1 through every map read (memory.md 7.1, 7.2, 8 and 11; SB-P1-05): get_relationship_graph and app_graph,
    // explore_graph, graph_changes, the stars save_insight and save_dashboard may cite, and dashboard_data's star feeds,
    // for every star there is and the queries that name a private record. Each reader's answers name only stars of the
    // scopes it reads, and links only between two of them; counts and pages come after the filter; and a star it can't
    // read answers exactly as one nobody has, never as a forbidden one.
    [PostgresFact] public async Task Every_map_answer_holds_only_the_readers_scopes_and_an_unreadable_star_answers_as_an_unknown_one()
    {
        var load = fixture.Load;
        var system = fixture.Readers["system"];
        var stars = await MapAnswers.Stars(load, system);
        // Whose each star is, as the engine's own principal reads them all.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        for (string? cursor = null; ;)
        {
            var page = await load.Host.Read(system, null, null, default, new GraphQuery(Cursor: cursor));
            foreach (var node in page.nodes) owners[node.id] = node.resource.owner;
            if (page.nextCursor is not { } next) break;
            cursor = next;
        }
        Assert.Contains(owners.Values, o => o == "jo");
        var queries = Scoped.PersonalQueries(Corpus);
        var run = DateTimeOffset.UtcNow.AddMinutes(-1);
        var party = load.GraphId("mem-sam-party");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var reader in Readers)
        {
            var answers = await MapAnswers.Of(load, fixture.Readers[reader], stars, queries, run.AddDays(-29), run);
            var readable = owners.Where(o => Reads(reader, o.Value)).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
            var hidden = owners.Keys.Where(id => !readable.Contains(id)).ToArray();
            Assert.Contains(owners, o => readable.Contains(o.Key) && o.Value == "household");
            if (ScopeCorpusFixture.Viewer(reader) is var own && own != "family") Assert.Contains(owners, o => readable.Contains(o.Key) && o.Value == own);
            Assert.NotEmpty(hidden);
            // A hidden star is absent from every answer not about that star itself (the ids a client says it knows come
            // back as removed, as unknown ones do), and every answer about it is the unknown star's.
            foreach (var star in hidden)
            {
                var quoted = Scoped.Quoted(star);
                foreach (var (op, text) in answers)
                    if (op != "graph overview, known" && !Scoped.AboutOneStar.Any(about => op == $"{about} {star}"))
                        Assert.False(text.Contains(quoted, StringComparison.Ordinal), $"{reader} is served {star} through {op}");
                Scoped.AnswersAsUnknown(answers, star, reader);
            }
            // The overview pages through exactly the readable stars, counted after the filter.
            using var overview = JsonDocument.Parse(answers["graph overview"]);
            var pages = overview.RootElement.EnumerateArray().ToArray();
            Assert.Equal(readable.Order(StringComparer.Ordinal), pages.SelectMany(p => p.GetProperty("nodes").EnumerateArray())
                .Select(n => n.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
            Assert.Contains($"of {readable.Count} readable records", pages[0].GetProperty("issues")[0].GetProperty("reason").GetString());
            Assert.Contains($"({readable.Count} readable in all)", pages[0].GetProperty("issues")[0].GetProperty("reason").GetString());
            Assert.Equal(readable.Order(StringComparer.Ordinal), JsonSerializer.Deserialize<string[]>(answers["known"]));
            // Story 1: Sam's surprise party for Alex is on Sam's map only.
            Assert.Equal(reader == "sam", readable.Contains(party));
            output.WriteLine($"{reader}: {readable.Count} stars, {hidden.Length} hidden, {answers.Count} answers");
        }
        output.WriteLine($"{Readers.Length} readers: {clock.ElapsedMilliseconds} ms");
    }

    // The re-attribution window (memory.md section 8): a record moved between passes, here a Family memory a pass now sees
    // in Alex's private scope, has two fresh rows for one star until the older lapses. Each read keeps the newest
    // observation before the reader's filter, so no reader is served the Family row any more: not while the newer is
    // fresh, nor once it is deleted at its source. With scopes off each read keeps the newest it is served, as before.
    [PostgresFact] public async Task A_star_re_attributed_between_passes_is_never_served_under_its_old_scope()
    {
        var load = fixture.Load;
        var system = fixture.Readers["system"];
        var memory = Corpus.Memories.First(m => m.Scope == "family" && ParityMapSource.Eligible(m, Corpus.Now));
        var star = load.GraphId(memory.Id);
        async Task<string?> Owner(string reader) =>
            (await load.Host.Read(fixture.Readers[reader], null, null, default, new GraphQuery("get", star))).nodes.SingleOrDefault()?.resource.owner;
        Assert.Equal("household", await Owner("sam"));
        var family = await Scoped.Snapshot(load.Db, system, star);
        var moved = await Scoped.Star(fixture.Source, family with { Key = family.Key with { Subject = "alex" }, Scope = "private", RetrievedAt = DateTimeOffset.UtcNow });
        var run = DateTimeOffset.UtcNow.AddMinutes(-1);
        try
        {
            Assert.Equal("alex", await Owner("alex"));
            foreach (var reader in new[] { "sam", "family", "app" })
            {
                Assert.Null(await Owner(reader));
                Scoped.AnswersAsUnknown(await MapAnswers.Of(load, fixture.Readers[reader], [star, MapAnswers.Unknown], [], run.AddDays(-29), run), star, reader);
            }
            await Scoped.Mode(fixture.Source, "off");
            Assert.Equal("alex", await Owner("sam"));
            await Scoped.Mode(fixture.Source, "on");
            // Deleted at its source after the move.
            await Scoped.Execute(fixture.Source, "UPDATE resources SET status='deleted' WHERE id=@id", ("id", moved));
            foreach (var reader in Readers) Assert.Null(await Owner(reader));
            await Scoped.Mode(fixture.Source, "off");
            Assert.Equal("household", await Owner("sam"));
        }
        finally
        {
            await Scoped.Mode(fixture.Source, "on");
            await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=@id", ("id", moved));
        }
        Assert.Equal("household", await Owner("sam"));
    }

    // A star is served to its record's readers (memory.md section 8), judged at each read: the catalog's star for a
    // save_document copy follows the copy rule, a data star its person's measurements (a source share included), and a
    // native memory's star the memory's own row, which a share or un-share changes in place, with no pass between.
    [PostgresFact] public async Task A_star_is_served_to_its_records_readers_at_each_read()
    {
        var load = fixture.Load;
        var source = fixture.Source;
        var now = DateTimeOffset.UtcNow;
        async Task<bool> Sees(string reader, string star) =>
            (await load.Host.Readable(fixture.Readers[reader], default)).View.Records.Any(r => r.Key.GraphId == star);
        async Task<string[]> Who(string star) => [.. (await Task.WhenAll(Readers.Select(async r => (r, await Sees(r, star)))))
            .Where(x => x.Item2).Select(x => x.r).Order(StringComparer.Ordinal)];
        var made = new List<Guid>();
        try
        {
            // Alex's private upload, and a household save_document copy of it; the catalog's document star for each.
            made.Add(await Scoped.Row(source, "alex", "documents", "private", "fx-map-upload", mode: "archived-original"));
            made.Add(await Scoped.Row(source, "household", "documents", "shared", "fx-map-upload", mode: "dated-snapshot"));
            DiscoveryRecord Document(string subject, string scope) => new(new(subject, CatalogStars.Provider, "documents", $"{subject}:fx-map-upload"),
                scope, "document", "DEMO · scope fixture", null, "catalog-sha256:fx-map", null, now, []);
            made.Add(await Scoped.Star(source, Document("alex", "private")));
            made.Add(await Scoped.Star(source, Document("household", "shared")));
            var upload = Document("alex", "private").Key.GraphId;
            var copy = Document("household", "shared").Key.GraphId;
            Assert.Equal(["alex"], await Who(upload));
            Assert.Equal(["alex"], await Who(copy));
            // Alex's heart data: hers, until she shares her CSV imports with Family.
            var heart = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount(CsvAdapter.Provider), "alex:heart").GraphId;
            Assert.Equal(["alex"], await Who(heart));
            await Scoped.Execute(source, "INSERT INTO source_shares(person_id,provider) VALUES('alex',@p)", ("p", CsvAdapter.Provider));
            try { Assert.Equal(["alex", "app", "family", "sam"], await Who(heart)); }
            finally { await Scoped.Execute(source, "DELETE FROM source_shares WHERE person_id='alex'"); }
            Assert.Equal(["alex"], await Who(heart));
            // A Family memory Alex wrote and its star, which a pass saved while it was Family. Alex un-shares it: no pass
            // has run, the star's row still says Family, and Sam's next read finds no star.
            var memory = await MemoryRows.Memory(source, "household", "alex", "DEMO: the fictional shed key hangs by the door.");
            var star = new DiscoveryRecord(new("household", "memory", "memory", memory.ToString()), "shared", "memory", "DEMO · a Family memory",
                null, "r1", null, now, []);
            made.Add(await Scoped.Star(source, star));
            Assert.Equal(["alex", "app", "family", "sam"], await Who(star.Key.GraphId));
            await Scoped.Execute(source, "UPDATE resources SET subject_id='alex',scope='private' WHERE id=@id", ("id", memory));
            Assert.Equal(["alex"], await Who(star.Key.GraphId));
            await Scoped.Execute(source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", memory));
            Assert.Equal(["alex", "app", "family", "sam"], await Who(star.Key.GraphId));
            // With scopes off every star is every reader's, as before.
            await Scoped.Mode(source, "off");
            foreach (var each in new[] { upload, copy, heart, star.Key.GraphId }) Assert.Equal(["alex", "app", "family", "sam"], await Who(each));
        }
        finally
        {
            await Scoped.Mode(source, "on");
            await Scoped.Execute(source, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", made.ToArray()));
        }
    }

    [PostgresFact] public async Task A_cached_copy_narrows_with_its_original_at_the_next_read()
    {
        // Every save_document copy is a cache of what a source said. Here a Family note's copy, read by Sam, until the
        // source moves the note into Alex's private scope: the next pass writes its row under Alex, and Sam's next read
        // of the copy finds nothing, with no rebuild in between.
        var retrieval = fixture.Retrieval;
        var note = Corpus.Notes.First(n => n.Scope == "family");
        var copy = fixture.Load.ResourceIds[note.Id];
        Assert.NotEqual("[]", await retrieval.Resource(fixture.Readers["sam"], copy, default));
        var moved = await Scoped.Row(fixture.Source, "alex", ParityMapSource.Provider, "private", "scope-moved-" + note.Id, native: ParityLoader.FilePrefix + note.Id);
        try
        {
            Assert.Equal("[]", await retrieval.Resource(fixture.Readers["sam"], copy, default));
            Assert.Equal("[]", await retrieval.Resource(fixture.Readers["family"], copy, default));
            Assert.NotEqual("[]", await retrieval.Resource(fixture.Readers["alex"], copy, default));
        }
        finally { await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=@id", ("id", moved)); }
        Assert.NotEqual("[]", await retrieval.Resource(fixture.Readers["sam"], copy, default));
    }

    // SB-P1-04, the reads of memory.md 7.4, each read as its caller. Rows a test adds are removed again, so the class's
    // other tests see the corpus as it was loaded. Each test also reads with scopes off, every install's mode, where every
    // reader is answered exactly as the engine's own principal, which reads every subject as these reads all did before.
    async Task Off(Func<Task> check)
    {
        await Scoped.Mode(fixture.Source, "off");
        try { await check(); }
        finally { await Scoped.Mode(fixture.Source, "on"); }
    }
    // An answer without the times a read stamps with its own clock (when it was read, and the window it ends now).
    static string Stable(JsonNode? answer)
    {
        var copy = answer?.DeepClone();
        void Strip(JsonNode? node)
        {
            if (node is JsonObject o) { foreach (var key in new[] { "asOf", "start", "end" }) o.Remove(key); foreach (var (_, v) in o) Strip(v); }
            else if (node is JsonArray a) foreach (var v in a) Strip(v);
        }
        Strip(copy);
        return copy?.ToJsonString() ?? "null";
    }

    // CatalogReader: the rebuild's inventory as the engine's own principal, which reads every subject; app_graph's content
    // and data as their reader. Each reader's inventory is the engine's, less the subjects it can't read.
    [PostgresFact] public async Task The_catalog_reads_each_record_and_series_as_its_reader()
    {
        var db = fixture.Load.Db;
        var catalog = (await fixture.Load.Host.Readable(fixture.Readers["system"], default)).Configuration.Catalog with { Scoped = true };
        static string[] Metrics(CatalogInventory i) => [.. i.Metrics.Select(m => $"{m.Subject} {m.Provider} {m.Scope} {m.Metric}").Order(StringComparer.Ordinal)];
        static string[] Documents(CatalogInventory i) => [.. i.Documents.Select(d => $"{d.Subject} {d.Provider} {d.ExternalId}").Order(StringComparer.Ordinal)];
        var all = await CatalogReader.Inventory(db, fixture.Readers["system"], catalog, default);
        foreach (var person in People)
        {
            Assert.Contains(all.Metrics, m => m.Subject == person);
            Assert.Contains(all.Documents, d => d.Subject == person);
        }
        foreach (var reader in Readers)
        {
            var mine = await CatalogReader.Inventory(db, fixture.Readers[reader], catalog, default);
            Assert.Equal(Metrics(all with { Metrics = [.. all.Metrics.Where(m => Reads(reader, m.Subject))] }), Metrics(mine));
            Assert.Equal(Documents(all with { Documents = [.. all.Documents.Where(d => Reads(reader, d.Subject))] }), Documents(mine));
        }
        // A record's text, by the row its star names: Alex's private memory is Alex's alone, a Family one everyone's.
        async Task<(string Subject, string Provider, string Scope, string External)> Row(string id)
        {
            var row = Rows(await db.Read(fixture.Readers["system"], "SELECT subject_id,provider,scope,external_id FROM resources WHERE id=@id", default,
                ("id", fixture.Load.ResourceIds[id])))[0];
            return (row.GetProperty("subject_id").GetString()!, row.GetProperty("provider").GetString()!, row.GetProperty("scope").GetString()!,
                row.GetProperty("external_id").GetString()!);
        }
        var alexs = await Row(Corpus.Memories.First(m => m.Scope == "personal:alex").Id);
        var familys = await Row(Corpus.Memories.First(m => m.Scope == "family").Id);
        foreach (var reader in Readers)
        {
            var principal = fixture.Readers[reader];
            Assert.Equal(Reads(reader, "alex"), await CatalogReader.Text(db, principal, alexs.Subject, alexs.Provider, alexs.Scope, alexs.External, default) is not null);
            Assert.NotNull(await CatalogReader.Text(db, principal, familys.Subject, familys.Provider, familys.Scope, familys.External, default));
            // A data star's series: each person's resting heart rate, read only by them.
            foreach (var person in People)
            {
                var series = await CatalogReader.Series(db, principal, catalog, person, CsvAdapter.Provider, "private",
                    catalog.Group(CsvAdapter.Provider, "resting_heart_rate").Id, default);
                Assert.Equal(Reads(reader, person), series.Any(s => s.days.Count > 0));
            }
        }
        await Off(async () =>
        {
            var unscoped = catalog with { Scoped = false };
            async Task<string[]> Answers(Guid principal) =>
            [
                .. Metrics(await CatalogReader.Inventory(db, principal, unscoped, default)), .. Documents(await CatalogReader.Inventory(db, principal, unscoped, default)),
                await CatalogReader.Text(db, principal, alexs.Subject, alexs.Provider, alexs.Scope, alexs.External, default) is { } text
                    ? $"{text.Text}|{text.Complete}|{text.Modified:O}|{text.Original}" : "no text",
                .. await Task.WhenAll(People.Select(async person => JsonSerializer.Serialize(await CatalogReader.Series(db, principal, unscoped, person,
                    CsvAdapter.Provider, "private", unscoped.Group(CsvAdapter.Provider, "resting_heart_rate").Id, default)))),
            ];
            var everything = await Answers(fixture.Readers["system"]);
            Assert.Contains(everything, a => a.StartsWith("alex ", StringComparison.Ordinal));
            foreach (var reader in Readers) Assert.Equal(everything, await Answers(fixture.Readers[reader]));
        });
    }

    // AppHealth: app_latest_glucose and app_recent_glucose read glucose as their caller, the default person is chosen
    // among the subjects the caller reads, and the tools are listed only to a caller that reads some glucose.
    [PostgresFact] public async Task The_apps_glucose_reads_only_the_callers_scopes()
    {
        var config = TestSettings.Use();
        var now = DateTimeOffset.UtcNow;
        var today = config.Today();
        // Alex's private glucose: a live feed for the last hour (pump-live) and three archived days before today (cgm).
        var live = await Scoped.Row(fixture.Source, "alex", "pump-live", "private", "scope-glucose-live");
        var archive = await Scoped.Row(fixture.Source, "alex", "cgm", "private", "scope-glucose-archive");
        await Scoped.Execute(fixture.Source, """
            INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
            SELECT @live,'glucose','live-'||i,@now-make_interval(mins=>5*i),@today,100+i,'mg/dL','source-reported' FROM generate_series(1,12) i
            """, ("live", live), ("now", now), ("today", today));
        await Scoped.Execute(fixture.Source, """
            INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
            SELECT @archive,'glucose','archive-'||i,@start+make_interval(mins=>5*i),(@start+make_interval(mins=>5*i))::date,110,'mg/dL','source-reported'
            FROM generate_series(0,863) i
            """, ("archive", archive), ("start", new DateTimeOffset(today.AddDays(-3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
        await Scoped.Execute(fixture.Source, "INSERT INTO ingest_freshness(plugin,provider,subject_id,last_accepted_at) VALUES('pump','pump-live','alex',now())");
        try
        {
            // One AppHealth for every reader, as the engine keeps one for every caller (a singleton): its glucose check
            // and default person are kept per caller, so a reader of Alex's glucose asking first leaves nothing for the
            // next reader to inherit.
            var health = new AppHealth(fixture.Load.Db, config);
            foreach (var reader in Readers.OrderByDescending(r => Reads(r, "alex")))
            {
                var principal = fixture.Readers[reader];
                var allowed = Reads(reader, "alex");
                Assert.Equal(allowed, await health.HasGlucose(principal, default));
                var latest = await health.Latest(principal, "alex", default);
                Assert.Equal(allowed ? 1 : 0, latest["rows"]!.AsArray().Count);
                var day = await health.Recent(principal, "alex", false, default);
                Assert.Equal(allowed ? 12 : 0, day["rows"]!.AsArray().Count);
                Assert.Equal(allowed, day["lastSyncedAt"] is not null);
                var fortnight = await health.Recent(principal, "alex", true, default);
                Assert.Equal(allowed, fortnight["series"]!.AsArray().Count > 0);
                Assert.Equal(allowed, fortnight["summary"] is not null);
                // The default person is chosen among those the caller reads; with none, nothing is read.
                var chosen = await health.Latest(principal, null, default);
                if (allowed) Assert.Equal("alex", (string?)chosen["subject"]);
                Assert.Equal(allowed ? 1 : 0, chosen["rows"]!.AsArray().Count);
                var listed = JsonSerializer.SerializeToNode(await AppEndpoint.Tools(health, principal, default))!.AsArray().Select(t => (string?)t!["name"]).ToArray();
                Assert.Equal(allowed, listed.Contains(AppEndpoint.LatestGlucose));
                Assert.Equal(allowed, listed.Contains(AppEndpoint.RecentGlucose));
            }
            await Off(async () =>
            {
                var health = new AppHealth(fixture.Load.Db, config);
                async Task<string[]> Answers(Guid principal)
                {
                    return [(await health.HasGlucose(principal, default)).ToString(), Stable(await health.Latest(principal, "alex", default)),
                        Stable(await health.Latest(principal, null, default)), Stable(await health.Recent(principal, "alex", false, default)),
                        Stable(await health.Recent(principal, "alex", true, default)), Stable(JsonSerializer.SerializeToNode(await AppEndpoint.Tools(health, principal, default)))];
                }
                var everything = await Answers(fixture.Readers["system"]);
                Assert.Equal("True", everything[0]);
                foreach (var reader in Readers) Assert.Equal(everything, await Answers(fixture.Readers[reader]));
            });
        }
        finally
        {
            await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", new[] { live, archive }));
            await Scoped.Execute(fixture.Source, "DELETE FROM ingest_freshness WHERE plugin='pump' AND provider='pump-live' AND subject_id='alex'");
        }
    }

    // AppFiles: GET /app/documents/<id> serves an archived original only to a caller that reads its record; to any other
    // it answers exactly as for an id nobody has.
    [PostgresFact] public async Task The_document_route_serves_an_original_only_to_its_readers()
    {
        var config = TestSettings.Use();
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-scope-archive-" + Guid.NewGuid().ToString("N")[..10]);
        var archive = new Archive(folder);
        var files = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (subject, scope) in new[] { ("alex", "private"), ("household", "shared") })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"DEMO fictional upload of {subject}");
            var saved = await archive.Save(bytes);
            var id = files[subject] = Guid.NewGuid();
            await Scoped.Execute(fixture.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,raw_path,checksum,status,metadata)
                VALUES(@id,@s,'documents',@e,'document',@scope,'DEMO · scope upload','DEMO fictional upload',@raw,@sum,'current',
                  jsonb_build_object('fileName','DEMO upload.txt','format','text','bytes',@bytes::text,'retrievalMode','archived-original'))
                """, ("id", id), ("s", subject), ("e", "file:scope-upload-" + subject), ("scope", scope), ("raw", saved.Path),
                ("sum", saved.Checksum), ("bytes", bytes.Length));
        }
        async Task<(int Status, string Body)> Get(Guid principal, Guid id)
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "GET";
            context.Response.Body = new MemoryStream();
            await AppFiles.Handle(context, principal, id, fixture.Load.Db, archive, config);
            return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        }
        try
        {
            foreach (var reader in Readers)
            {
                var unknown = await Get(fixture.Readers[reader], Guid.NewGuid());
                Assert.Equal(404, unknown.Status);
                foreach (var (subject, id) in files)
                {
                    var served = await Get(fixture.Readers[reader], id);
                    if (Reads(reader, subject)) Assert.Equal((200, $"DEMO fictional upload of {subject}"), served);
                    else Assert.Equal(unknown, served);
                }
            }
            await Off(async () =>
            {
                foreach (var reader in Readers.Append("system"))
                    foreach (var (subject, id) in files)
                        Assert.Equal((200, $"DEMO fictional upload of {subject}"), await Get(fixture.Readers[reader], id));
            });
        }
        finally
        {
            await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", files.Values.ToArray()));
            Directory.Delete(folder, true);
        }
    }

    // GarminTiles: app_garmin_summary reads the Garmin owner's rows, and their sync state, as its caller.
    [PostgresFact] public async Task The_Garmin_summary_reads_its_owners_rows_as_the_caller()
    {
        var config = TestSettings.Use();
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-scope-garmin-" + Guid.NewGuid().ToString("N")[..10]);
        var garmin = new Knowledge.Adapters.Garmin.GarminAdapter(new FakeGarmin(), (_, _) => Task.CompletedTask);
        garmin.Configure(new(new AdapterSettings("garmin", new Dictionary<string, object?>
            { ["enabled"] = "true", ["owner"] = "alex", ["importPath"] = folder }), config, fixture.Load.Db, new Archive(folder)));
        var today = garmin.Today();
        var day = await Scoped.Row(fixture.Source, "alex", "garmin", "private", "scope-garmin-day");
        var job = Guid.NewGuid();
        await Scoped.Execute(fixture.Source, "UPDATE resources SET kind='metric',local_date=@d,timezone='Europe/London' WHERE id=@id", ("id", day), ("d", today));
        await Scoped.Execute(fixture.Source, """
            INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
            VALUES(@id,'steps','day',NULL,@d,10500,'count','source-reported')
            """, ("id", day), ("d", today));
        await Scoped.Execute(fixture.Source, """
            INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,status) VALUES(@job,'alex','garmin',@d,@d,@d,'complete')
            """, ("job", job), ("d", today));
        await Scoped.Execute(fixture.Source, "INSERT INTO ingest_freshness(plugin,provider,subject_id,last_accepted_at) VALUES('garmin','garmin','alex',now())");
        try
        {
            foreach (var reader in Readers)
            {
                var summary = await Knowledge.Adapters.Garmin.GarminTiles.Read(garmin, fixture.Readers[reader], default);
                Schemas.Valid(Schemas.Folder("mcp"), "app_garmin_summary.output.schema.json", summary);
                var allowed = Reads(reader, "alex");
                Assert.Equal(allowed, summary["rows"]!.AsArray().Any(r => (string?)r!["metric"] == "steps"));
                Assert.Equal(allowed, summary["lastSyncedAt"] is not null);
                if (!allowed)
                {
                    Assert.Empty(summary["rows"]!.AsArray());
                    Assert.False((bool)summary["coverageComplete"]!);
                    Assert.Equal(0L, (long)summary["activityCount"]!);
                }
            }
            // app_request_garmin_sync answers with the owner's sync jobs, so it takes the same predicate: the household
            // view can't read Alex's Garmin and is refused, queuing nothing; Alex's own viewer session is answered.
            var viewer = await Scoped.Principal(fixture.Source, "scope-viewer-alex", "app", "alex");
            var jobs = (long)(await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM sync_jobs WHERE subject_id='alex' AND provider='garmin'"))!;
            var refused = await Assert.ThrowsAsync<PostgresException>(() => Knowledge.Adapters.Garmin.GarminTiles.Request(garmin, fixture.Readers["app"], default));
            Assert.Equal("42501", refused.SqlState);
            Assert.Equal(jobs, (long)(await Scoped.Scalar(fixture.Source, "SELECT count(*) FROM sync_jobs WHERE subject_id='alex' AND provider='garmin'"))!);
            var asked = await Knowledge.Adapters.Garmin.GarminTiles.Request(garmin, viewer, default);
            Schemas.Valid(Schemas.Folder("mcp"), "app_request_garmin_sync.output.schema.json", asked);
            Assert.NotNull(asked["lastSyncedAt"]);
            await Scoped.Execute(fixture.Source, "DELETE FROM sync_jobs WHERE subject_id='alex' AND provider='garmin' AND metadata->>'reason'='on-demand'");
            await Off(async () =>
            {
                var everything = Stable(await Knowledge.Adapters.Garmin.GarminTiles.Read(garmin, fixture.Readers["system"], default));
                Assert.Contains("\"steps\"", everything);
                foreach (var reader in Readers)
                    Assert.Equal(everything, Stable(await Knowledge.Adapters.Garmin.GarminTiles.Read(garmin, fixture.Readers[reader], default)));
                // With scopes off the household view's request is answered, as before.
                Assert.NotNull((await Knowledge.Adapters.Garmin.GarminTiles.Request(garmin, fixture.Readers["app"], default))["lastSyncedAt"]);
            });
        }
        finally
        {
            await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=@id", ("id", day));
            await Scoped.Execute(fixture.Source, "DELETE FROM sync_jobs WHERE id=@id OR (subject_id='alex' AND provider='garmin' AND metadata->>'reason'='on-demand')", ("id", job));
            await Scoped.Execute(fixture.Source, "DELETE FROM ingest_freshness WHERE plugin='garmin' AND provider='garmin' AND subject_id='alex'");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // DashboardFeeds (memory.md 7.3): a data feed's rows and a log feed's entries are read as the reader, and a log feed is
    // checked against its subject as every other feed is against its star: a reader who can't read the subject gets the
    // same "unavailable" whether the log has entries or none.
    [PostgresFact] public async Task Each_dashboard_feed_reads_as_its_reader_and_a_log_is_checked_against_its_subject()
    {
        var load = fixture.Load;
        var feeds = new DashboardFeeds(load.Db, load.Host, load.Registry);
        // Every data star, with whose it is, as the engine's own principal reads them.
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        for (string? cursor = null; ;)
        {
            var page = await load.Host.Read(fixture.Readers["system"], null, null, default, new GraphQuery(Cursor: cursor));
            foreach (var node in page.nodes.Where(n => n.resource.identity.account.StartsWith("data:", StringComparison.Ordinal)))
                data[node.id] = node.resource.owner;
            if (page.nextCursor is not { } next) break;
            cursor = next;
        }
        Assert.Contains(data.Values, o => o == "alex");
        Assert.Contains(data.Values, o => o == "sam");
        // Alex reads first. The metrics a star lists are kept per reader, but only defensively: a reader is served a data
        // star exactly when it reads that star's rows, so no reader here could be handed another's list (memory.md 8).
        foreach (var reader in Readers)
            foreach (var (star, owner) in data)
            {
                var feed = (await feeds.Read(fixture.Readers[reader], [new("d", "data", star, Days: DashboardFeed.MaxDays)], false, default))["feeds"]!["d"]!;
                Assert.Equal(Reads(reader, owner) ? "ok" : "unavailable", (string?)feed["status"]);
                if (Reads(reader, owner)) Assert.NotEmpty(feed["rows"]!.AsArray());
                else Assert.Null(feed["rows"]);
            }
        // Log entries: one in Alex's private log, one in a Family log. Sam's log of the same name has none.
        var entries = new List<Guid>();
        foreach (var (subject, scope) in new[] { ("alex", "private"), ("household", "shared") })
        {
            var entry = await Scoped.Row(fixture.Source, subject, "log", scope, "scope-mood:" + subject);
            entries.Add(entry);
            await Scoped.Execute(fixture.Source, """
                UPDATE resources SET kind='log-entry',local_date=current_date,
                  metadata=jsonb_build_object('log','scope-mood','type','note','note','DEMO fictional note') WHERE id=@id
                """, ("id", entry));
        }
        async Task<JsonNode> Log(string reader, string log, string subject) =>
            (await feeds.Read(fixture.Readers[reader], [new("l", "log", null!, Log: log, Subject: subject, Days: 30)], false, default))["feeds"]!["l"]!;
        try
        {
            foreach (var reader in Readers)
            {
                foreach (var subject in new[] { "alex", "household" })
                {
                    var feed = await Log(reader, "scope-mood", subject);
                    if (Reads(reader, subject))
                    {
                        Assert.Equal("ok", (string?)feed["status"]);
                        Assert.Equal("DEMO fictional note", (string?)Assert.Single(feed["entries"]!.AsArray())!["note"]);
                    }
                    else
                    {
                        Assert.Equal(("unavailable", "This log belongs to a scope you can't read."), ((string?)feed["status"], (string?)feed["reason"]));
                        Assert.Null(feed["entries"]);
                        var empty = await Log(reader, "scope-empty", subject);
                        Assert.Equal((feed["status"]!.ToJsonString(), feed["reason"]!.ToJsonString()), (empty["status"]!.ToJsonString(), empty["reason"]!.ToJsonString()));
                    }
                }
                var sams = await Log(reader, "scope-mood", "sam");
                Assert.Equal(Reads(reader, "sam") ? "ok" : "unavailable", (string?)sams["status"]);
                if (Reads(reader, "sam")) Assert.Empty(sams["entries"]!.AsArray());
            }
            await Off(async () =>
            {
                Assert.Contains("DEMO fictional note", Stable(await Log("system", "scope-mood", "alex")));
                foreach (var subject in new[] { "alex", "sam", "household" })
                {
                    var everything = Stable(await Log("system", "scope-mood", subject));
                    foreach (var reader in Readers) Assert.Equal(everything, Stable(await Log(reader, "scope-mood", subject)));
                }
                foreach (var star in data.Keys)
                {
                    async Task<string> Feed(Guid principal) =>
                        Stable((await feeds.Read(principal, [new("d", "data", star, Days: DashboardFeed.MaxDays)], false, default))["feeds"]!["d"]);
                    var everything = await Feed(fixture.Readers["system"]);
                    Assert.Contains("\"ok\"", everything);
                    foreach (var reader in Readers) Assert.Equal(everything, await Feed(fixture.Readers[reader]));
                }
            });
        }
        finally { await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", entries.ToArray())); }
    }
}

// The corpus's map source as an adapter that keeps to its pass's budget, as the real ones do (the stand-in lists
// everything): in its listing order, at most graph.maxMemories records in all, or with scopes on (SourcePass.MemoryShares)
// at most each subject's share of them; and, given `without`, none of that subject's, as if the source held none.
internal sealed class BudgetedSource(IMemorySource inner, string? without = null) : IMemorySource
{
    public string Name => inner.Name;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers => inner.Providers;
    public async Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var listing = await inner.List(pass, ct);
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<DiscoveryRecord>();
        var cut = false;
        foreach (var r in listing.Records.Where(r => r.Key.Subject != without))
        {
            var mine = taken.GetValueOrDefault(r.Key.Subject);
            if (pass.MemoryShares is null ? kept.Count >= pass.MaxMemories : mine >= pass.MemoriesOf(r.Key.Subject)) { cut = true; continue; }
            kept.Add(r);
            taken[r.Key.Subject] = mine + 1;
        }
        return new(kept, listing.Complete && !cut);
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => inner.ReadText(record, ct);
}

// The map per reader with scopes on (memory.md 6.2 and section 8; SB-P1-05, SB-P1-17): Sam's map, every answer of it,
// revisions and graph_changes included, is the same whether Alex's private scope holds anything or nothing, its budgets
// per scope included, and with a Family memory that links to one of Alex's private memories. A class fixture is the
// class's own, so this corpus is its own: the test rebuilds the map and takes Alex's rows away.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryScopeMapTests(ScopeCorpusFixture fixture, ITestOutputHelper output) : IClassFixture<ScopeCorpusFixture>
{
    // The corpus with a link each way between the first Family memory the map lists and the first of Alex's private ones
    // (links only Alex is served, whose Family end Sam reads), and the two memories' stars.
    static (ParityCorpus Corpus, string Family, string Alex) Linked(ParityCorpus corpus)
    {
        bool Listed(ParityMemory m, string subject) => ParityLoader.Subject(m.Scope) == subject && ParityMapSource.Eligible(m, corpus.Now);
        var family = corpus.Memories.First(m => Listed(m, RhythmConfig.Household));
        var alex = corpus.Memories.First(m => Listed(m, "alex"));
        string Star(ParityMemory m) => new ParityMapSource(corpus).Key(m.Scope, m.Id, "memory").GraphId;
        return (corpus with { Memories = [.. corpus.Memories.Select(m =>
            m.Id == family.Id ? m with { Links = [.. m.Links, new("relates_to", alex.Id, "explicit")] }
            : m.Id == alex.Id ? m with { Links = [.. m.Links, new("supports", family.Id, "explicit")] } : m)] }, Star(family), Star(alex));
    }
    // The corpus as if Alex's private scope held nothing.
    static ParityCorpus WithoutAlex(ParityCorpus corpus) => corpus with
    {
        Memories = [.. corpus.Memories.Where(m => ParityLoader.Subject(m.Scope) != "alex")],
        Notes = [.. corpus.Notes.Where(n => ParityLoader.Subject(n.Scope) != "alex")],
        Documents = [.. corpus.Documents.Where(d => ParityLoader.Subject(d.Scope) != "alex")],
    };

    [PostgresFact] public async Task Sams_map_is_the_same_with_and_without_Alexs_private_rows()
    {
        var load = fixture.Load;
        var source = fixture.Source;
        var system = fixture.Readers["system"];
        var config = TestSettings.Use();
        var state = Path.Combine(load.State, "budgeted");
        var (linked, familyStar, alexStar) = Linked(load.Corpus);
        // graph.maxMemories of 80, so the budget binds: with scopes on, 20 for each of the catalog's four subjects. Without
        // Alex's rows the source holds none of hers, so the Family memory's link to hers is gone too.
        (RelationshipHost Host, AdapterRegistry Registry) Map(string? without)
        {
            var registry = new AdapterRegistry([new BudgetedSource(new ParityMapSource(without is null ? linked : WithoutAlex(linked)), without), new CsvAdapter()]);
            return (new RelationshipHost(load.Db, () => RelationshipHostConfiguration.From(config, system, state) with { MaxMemories = 80 },
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(load.Db, c.Principal, wanted, ct),
                    load.Embeddings.Model is null ? null : load.Embeddings.Vector, load.Embeddings.Id), registry), registry);
        }
        var run = DateTimeOffset.UtcNow.AddMinutes(-1);
        var since = run.AddDays(-29);
        var queries = Scoped.PersonalQueries(load.Corpus, "alex");
        // Every star the map held before, Alex's among them, built while the household shared everything: with scopes off
        // they reach Sam's map, so a comparison of Sam's answers can see them.
        var stars = await MapAnswers.Stars(load, system);
        await Scoped.Mode(source, "off");
        var shared = (await load.Host.Read(fixture.Readers["sam"], null, null, default)).nodes;
        Assert.Contains(shared, n => n.resource.owner == "alex");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // A rebuild with scopes on an hour after the last (every row it does not list again has aged off), then every one
        // of Sam's answers.
        async Task<SortedDictionary<string, string>> Sams(string? without)
        {
            await Scoped.Mode(source, "on");
            await Scoped.Execute(source, """
                UPDATE resources SET relationship_snapshot=jsonb_set(relationship_snapshot,'{RetrievedAt}',
                  to_jsonb((relationship_snapshot->>'RetrievedAt')::timestamptz-interval '1 hour')) WHERE relationship_snapshot ? 'RetrievedAt'
                """);
            var (host, registry) = Map(without);
            Assert.Equal("complete", await host.Refresh(false, default));
            output.WriteLine($"{(without is null ? "with" : "without")} Alex's rows, rebuilt at {clock.ElapsedMilliseconds} ms: {string.Join("; ", host.Notes)}");
            var answers = await MapAnswers.Of(host, load.Db, registry, fixture.Readers["sam"], stars, queries, since, run);
            output.WriteLine($"{answers.Count} answers at {clock.ElapsedMilliseconds} ms");
            return answers;
        }
        try
        {
            var with = await Sams(null);
            // Each subject keeps its own share of the memory budget: 20 of the source's stars of the household, 20 of Sam's.
            var nodes = JsonDocument.Parse(with["graph overview"]).RootElement.EnumerateArray().SelectMany(p => p.GetProperty("nodes").EnumerateArray())
                .Where(n => n.GetProperty("provenance").GetProperty("sourceId").GetString() == ParityMapSource.Provider)
                .GroupBy(n => n.GetProperty("resource").GetProperty("owner").GetString()!).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(new Dictionary<string, int> { ["household"] = 20, ["sam"] = 20 }, nodes);
            // The links between the Family memory and Alex's are on the map, served to Alex alone.
            var alexSees = await load.Host.Read(fixture.Readers["alex"], null, null, default, new GraphQuery("neighbors", familyStar));
            Assert.Equal(2, alexSees.edges.Count(e => e.source == alexStar || e.target == alexStar));
            Assert.Contains(Scoped.Quoted(familyStar), with[$"graph get {familyStar}"]);
            Assert.DoesNotContain(alexStar, with[$"graph neighbors {familyStar}"]);
            // Alex's rows go for good, with their text, passages, links and stars; the source no longer lists any.
            await Scoped.Execute(source, """
                DELETE FROM resource_dependencies WHERE child_id IN (SELECT id FROM resources WHERE subject_id='alex')
                  OR parent_id IN (SELECT id FROM resources WHERE subject_id='alex')
                """);
            Assert.True(await Scoped.Execute(source, "DELETE FROM resources WHERE subject_id='alex'") > 0);
            var without = await Sams("alex");
            output.WriteLine($"{with.Count} answers each over {stars.Length} stars and {queries.Length} queries");
            Assert.True(with.SequenceEqual(without), MapAnswers.Difference(with, without));
        }
        finally { await Scoped.Mode(source, "on"); }
    }
}

// With scopes on, a household save_document copy of Alex's private original (every copy saved before the switch is a
// household one, memory.md 6.2) is served to Alex's readers only, by the copy rule; so it uses up Alex's budgets and is
// compared for similarity within Alex's scope, never the household's (memory.md section 8, Budgets). Sam's map, every
// answer of it, is the same with none of them and with more of them than the household's share of the catalog's saved
// documents. A class fixture is the class's own, so this corpus is its own: the test adds the copies.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryScopeCopyBudgetTests(ScopeCorpusFixture fixture, ITestOutputHelper output) : IClassFixture<ScopeCorpusFixture>
{
    [PostgresFact] public async Task Sams_map_is_the_same_with_and_without_household_copies_of_Alexs_private_files()
    {
        var load = fixture.Load;
        var source = fixture.Source;
        var system = fixture.Readers["system"];
        var catalog = RelationshipCatalog.For(TestSettings.Use());
        // More copies than the household's share of the 500 saved documents the catalog stars.
        var copies = catalog.Share(CatalogReader.MaxDocuments, RhythmConfig.Household) + 10;
        static string Plan(int i) => $"DEMO: a fictional allotment plan, number {i}, with the watering rota, the seed order for the spring beds and the shed keys.";
        // Family's own saved documents, which the catalog stars (the corpus's copies are shown by the map's records
        // instead), changed before the copies: if the copies took the household's share they would push these off.
        for (var i = 0; i < 5; i++)
            await Scoped.Execute(source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,revision,status,metadata,source_updated_at)
                VALUES(@id,'household','documents',@e,'document','shared',@t,@c,'r1','current',jsonb_build_object('retrievalMode','archived-original'),
                  now()-interval '30 days')
                """, ("id", Guid.NewGuid()), ("e", $"family-plan-{i}"), ("t", $"DEMO · Family allotment plan {i}"), ("c", Plan(i)));
        var run = DateTimeOffset.UtcNow.AddMinutes(-1);
        var since = run.AddDays(-29);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // A rebuild an hour after the last (every row it does not list again has aged off).
        async Task Rebuild(string what)
        {
            await Scoped.Execute(source, """
                UPDATE resources SET relationship_snapshot=jsonb_set(relationship_snapshot,'{RetrievedAt}',
                  to_jsonb((relationship_snapshot->>'RetrievedAt')::timestamptz-interval '1 hour')) WHERE relationship_snapshot ? 'RetrievedAt'
                """);
            Assert.Equal("complete", await load.Host.Refresh(false, default));
            output.WriteLine($"{what}, rebuilt at {clock.ElapsedMilliseconds} ms: {string.Join("; ", load.Host.Notes)}");
        }
        await Rebuild("without the copies");
        // Every star there is, and each copy's catalog star, asked about by id in both runs.
        static string CopyStar(int i) => new ResourceKey(RhythmConfig.Household, CatalogStars.Provider, DocumentOriginals.Provider,
            $"{RhythmConfig.Household}:alex-file-{i}").GraphId;
        string[] stars = [.. (await MapAnswers.Stars(load, system)).Concat(Enumerable.Range(0, copies).Select(CopyStar)).Distinct()];
        var queries = Scoped.PersonalQueries(load.Corpus, "alex").Append("allotment plan").ToArray();
        var sam = fixture.Readers["sam"];
        var without = await MapAnswers.Of(load, sam, stars, queries, since, run);
        // Family's documents are on Sam's map, so a comparison can see them go.
        Assert.Contains("Family allotment plan 4", without["graph overview"]);
        // Alex's private Drive files (their star rows, which no pass lists now), and a household copy of each, saved after
        // them and changed later than Family's documents.
        for (var i = 0; i < copies; i++)
        {
            await Scoped.Row(source, "alex", "drive", "private", $"alex-drive-{i}", native: $"alex-file-{i}");
            await Scoped.Execute(source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,revision,status,metadata,source_updated_at)
                VALUES(@id,'household','documents',@e,'document','shared',@t,@c,'r1','current',jsonb_build_object('retrievalMode','dated-snapshot'),now())
                """, ("id", Guid.NewGuid()), ("e", $"alex-file-{i}"), ("t", $"DEMO · Alex's allotment plan {i}"), ("c", Plan(100 + i)));
        }
        await Rebuild("with the copies");
        var with = await MapAnswers.Of(load, sam, stars, queries, since, run);
        output.WriteLine($"{with.Count} answers each over {stars.Length} stars and {queries.Length} queries, at {clock.ElapsedMilliseconds} ms");
        Assert.True(without.SequenceEqual(with), MapAnswers.Difference(without, with));
        // Alex is served the copies, as many as Alex's own share leaves room for, and each as a household star.
        var alexNodes = (await load.Host.Read(fixture.Readers["alex"], null, null, default, new GraphQuery("search", "Alex's allotment plan"))).nodes;
        Assert.NotEmpty(alexNodes);
        Assert.All(alexNodes, n => Assert.Equal(RhythmConfig.Household, n.resource.owner));
        // The rebuild budgeted each copy under Alex: its star names Alex as its one reader.
        Assert.Equal("alex", (await Scoped.Snapshot(load.Db, system, CopyStar(copies - 1))).Reader);
    }
}

// Ranking statistics and candidate windows per reader (memory.md 6.2): with scopes on, Sam's search answers do not
// depend on what Alex's private scope holds. A class fixture is the class's own, so this corpus is its own: the test
// takes Alex's rows away.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryScopeStatisticsTests(ScopeCorpusFixture fixture, ITestOutputHelper output) : IClassFixture<ScopeCorpusFixture>
{
    [PostgresFact] public async Task Sams_search_answers_are_the_same_with_and_without_Alexs_private_rows()
    {
        var retrieval = fixture.Retrieval;
        var sam = fixture.Readers["sam"];
        var queries = Scoped.PersonalQueries(fixture.Load.Corpus, "alex");
        async Task<string[]> Answers()
        {
            var answers = new List<string>();
            foreach (var q in queries)
            {
                answers.Add(string.Join(",", JsonDocument.Parse(await retrieval.Search(sam, q, 50, default)).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetGuid())));
                answers.Add(string.Join(",", JsonDocument.Parse(await retrieval.SearchResources(sam, q, "", "", "", "", 50, default)).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetGuid())));
                if (fixture.Load.Embeddings.Model is { } model)
                    answers.Add(string.Join(",", JsonDocument.Parse(await new SemanticSearch(fixture.Load.Db, model).Search(sam, q, "", 20, default)).RootElement.EnumerateArray()
                        .Select(r => r.GetProperty("id").GetGuid() + ":" + r.GetProperty("chunk_ordinal").GetInt32())));
            }
            return [.. answers];
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Scoped.Mode(fixture.Source, "off");
        var shared = await Answers();
        output.WriteLine($"scopes off: {clock.ElapsedMilliseconds} ms for {queries.Length} queries");
        await Scoped.Mode(fixture.Source, "on");
        clock.Restart();
        var with = await Answers();
        output.WriteLine($"scopes on: {clock.ElapsedMilliseconds} ms for {queries.Length} queries");
        // Alex's rows do reach these queries when the household shares everything, so the comparison can see them.
        Assert.NotEqual(shared, with);
        // Alex's rows go for good, with their text, passages and links, not only out of every read: a statistic counted
        // over rows of any status would still see marked ones.
        await Scoped.Execute(fixture.Source, """
            DELETE FROM resource_dependencies WHERE child_id IN (SELECT id FROM resources WHERE subject_id='alex')
              OR parent_id IN (SELECT id FROM resources WHERE subject_id='alex')
            """);
        Assert.True(await Scoped.Execute(fixture.Source, "DELETE FROM resources WHERE subject_id='alex'") > 0);
        Assert.Equal(with, await Answers());
    }
}
