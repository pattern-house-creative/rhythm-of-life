using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// The synthetic Second Brain export fixtures (SB-P8-02) are the importer's test inputs (SB-P8-01), so they are checked like
// code: the brain is a deployment upstream could hold, each export file is exactly what upstream's GET /export returns
// from it, upstream's own import accepts every row, every field the export leaves out is named, the cases the import
// must handle are there, and nothing in them is real. There is no importer yet.
//
// To change the fixtures, edit sb-export-brain.json, then run this class with RHYTHM_SB_EXPORT_WRITE=<folder>: it writes
// each export file, derived from the brain, into that folder, to copy over the checked-in ones.
public sealed partial class SecondBrainExportTests(ITestOutputHelper output)
{
    static readonly SbBrain Brain = SbBrain.Load();
    static readonly string[] EntryColumns = ["id", "content", "tags", "source", "created_at", "updated_at", "vector_ids", "recall_count",
        "importance_score", "contradiction_wins", "contradiction_losses", "workspace_id", "actor_id", "valid_from", "valid_until",
        "when_at", "when_kind", "when_source", "when_label", "staleness_checked_at"];
    static readonly string[] EdgeColumns = ["id", "source_id", "target_id", "type", "weight", "provenance", "metadata", "created_at", "updated_at", "workspace_id"];
    static readonly string[] ProjectColumns = ["id", "workspace_id", "name", "description", "aliases", "status", "created_at", "updated_at"];
    const string AlexFile = "sb-export-alex.v3.json", SamFile = "sb-export-sam.v3.json", OldFile = "sb-export-alex.v2.json";

    static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(p => p.Key);
    static IEnumerable<string> Sorted(IEnumerable<string> items) => items.Order(StringComparer.Ordinal);
    static long Long(JsonNode? node) => long.Parse(node!.ToJsonString(), CultureInfo.InvariantCulture);
    static string Str(JsonNode? node) => node!.GetValue<string>();
    static JsonObject[] Rows(JsonObject export, string name) => export[name] is JsonArray rows ? [.. rows.Select(r => r!.AsObject())] : [];
    static HashSet<string> Ids(JsonObject export) => Rows(export, "entries").Select(e => Str(e["id"])).ToHashSet(StringComparer.Ordinal);
    static SbEntry Any(string id) => Brain.AllEntries.Single(e => e.Id == id);
    static string Team(string name) => Brain.Workspaces.Single(w => w.Name == name).Id;

    [Fact] public void The_brain_is_a_deployment_upstream_could_hold()
    {
        // Exactly upstream's columns, and the fixture's own key, note and expect.
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(ParityCorpus.Folder, SbBrain.File)))!.AsObject();
        string[] own = ["key", "note", "expect"];
        Assert.All(raw["entries"]!.AsArray(), e => Assert.Equal(Sorted(EntryColumns), Sorted(Keys(e).Except(own))));
        Assert.All(raw["trash"]!.AsArray(), t => Assert.Equal(Sorted(EntryColumns.Except(["content", "vector_ids"])), Sorted(Keys(t!["row"]))));
        Assert.All(raw["edges"]!.AsArray().Concat(raw["trash"]!.AsArray().SelectMany(t => t!["edges"]!.AsArray())),
            e => Assert.Equal(Sorted(EdgeColumns), Sorted(Keys(e))));
        Assert.All(raw["projects"]!.AsArray(), p => Assert.Equal(Sorted(ProjectColumns), Sorted(Keys(p))));

        var all = Brain.AllEntries.ToArray();
        var edges = Brain.Edges.Concat(Brain.Trash.SelectMany(t => t.Edges)).ToArray();
        Assert.Equal(all.Length, all.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Length, all.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(edges.Length, edges.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Brain.Users, u => Assert.Matches("^usr-" + UuidPattern + "$", u.Id));
        Assert.All(Brain.Workspaces, w => Assert.Matches("^ws-" + UuidPattern + "$", w.Id));
        // Capture makes a memory's id a uuid; one id was chosen by a client before 4.0, longer than upstream's 40-byte bound.
        var chosen = Assert.Single(all, e => !Regex.IsMatch(e.Id, "^" + UuidPattern + "$"));
        Assert.True(Encoding.UTF8.GetByteCount(chosen.Id) > 40 && chosen.CreatedAt < Brain.Upgrade, chosen.Key);

        // Each member has one personal workspace of their own; every member is on the first team, the deployment's.
        Assert.All(Brain.Users, u => Assert.Single(Brain.Memberships, m => m.UserId == u.Id && Brain.Workspace(m.WorkspaceId).Kind == "personal"));
        Assert.All(Brain.Workspaces.Where(w => w.Kind == "personal"), w => Assert.Single(Brain.Memberships, m => m.WorkspaceId == w.Id));
        Assert.All(Brain.Workspaces, w => Assert.Contains(w.Kind, (string[])["personal", "company"]));
        var first = Brain.Workspaces.Where(w => w.Kind == "company").MinBy(w => w.CreatedAt)!;
        Assert.All(Brain.Users, u => Assert.Contains(Brain.Memberships, m => m.UserId == u.Id && m.WorkspaceId == first.Id));
        Assert.All(Brain.Memberships, m => Assert.True(m.CreatedAt >= Brain.User(m.UserId).CreatedAt && m.CreatedAt >= Brain.Workspace(m.WorkspaceId).CreatedAt));
        Assert.All(Brain.Users, u => Assert.True(u.RemovedAt is null || u.RemovedAt > u.CreatedAt, u.Name));

        Assert.All(all, e =>
        {
            // Written by a member of the workspace it was written in, who had not left; only a system job writes with no
            // author, and only into a team. A personal workspace's memories are its owner's.
            var written = Brain.Workspace(Brain.WorkspaceAt(e, e.CreatedAt));
            if (e.ActorId == "") Assert.True(e.Source == "system" && written.Kind == "company", e.Key);
            else
            {
                Assert.Contains(Brain.Memberships, m => m.UserId == e.ActorId && m.WorkspaceId == written.Id && m.CreatedAt <= e.CreatedAt);
                Assert.False(Brain.User(e.ActorId).RemovedAt <= e.CreatedAt, e.Key);
            }
            if (Brain.Workspace(e.WorkspaceId).Kind == "personal")
                Assert.Equal(Brain.Memberships.Single(m => m.WorkspaceId == e.WorkspaceId).UserId, e.ActorId);
            Assert.True(e.CreatedAt <= Brain.Now && (e.UpdatedAt is null || e.UpdatedAt >= e.CreatedAt && e.UpdatedAt <= Brain.Now), e.Key);
            Assert.True(e.Content.Trim().Length > 0 && Encoding.UTF8.GetByteCount(e.Content) <= 131_072, e.Key);

            // Tags as upstream stores them: trimmed, no repeats, at most one status, kind and volatility, each a known value.
            Assert.All(e.Tags, t => Assert.True(t.Length > 0 && t == t.Trim(), $"{e.Key}: '{t}'"));
            Assert.Equal(e.Tags.Length, e.Tags.Distinct(StringComparer.Ordinal).Count());
            foreach (var (prefix, values) in new[] { ("status:", SbTags.Statuses), ("kind:", SbTags.Kinds), ("volatility:", SbTags.Volatilities) })
            {
                var found = e.Tags.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                Assert.True(found.Length <= 1, $"{e.Key}: {prefix}");
                Assert.All(found, t => Assert.Contains(t[prefix.Length..], values));
            }
            Assert.InRange(e.ImportanceScore, 0, 5);
            Assert.True(e.RecallCount >= 0 && e.ContradictionWins >= 0 && e.ContradictionLosses >= 0, e.Key);
            // Validity dates are for what has happened, and a window never ends before it starts (importedWindow).
            Assert.False(e.ValidFrom > Brain.Now || e.ValidUntil > Brain.Now, e.Key);
            if (e.ValidUntil is { } until) Assert.True(until >= (e.ValidFrom ?? e.CreatedAt), e.Key);
            Assert.Equal(e.WhenAt is null, e.WhenKind is null);
            Assert.Equal(e.WhenAt is null, e.WhenSource is null);
            if (e.WhenKind is { } kind) Assert.Contains(kind, (string[])["due", "event", "wake"]);
            if (e.WhenSource is { } source) Assert.Contains(source, (string[])["explicit", "regex", "model"]);

            // A held row was never classified or embedded: a draft with no kind, no importance and no vectors, with its held
            // event at capture. Every other row was classified and embedded, under ids that start with its own.
            if (e.HeldReason is { } reason)
            {
                Assert.True(e.Status == "draft" && e.Kind is null && e.ImportanceScore == 0 && e.VectorIds.Length == 0, e.Key);
                Assert.Contains(Brain.Events, ev => ev.EntryId == e.Id && ev.Event == "held" && ev.CreatedAt == e.CreatedAt
                    && ev.Payload["reasons"]!.AsArray().Select(r => Str(r)).Contains(reason));
            }
            else
            {
                Assert.True(e.Kind is not null && e.Status is not null && e.VectorIds.Length > 0, e.Key);
                Assert.All(e.VectorIds, v => Assert.True(v == e.Id || v.StartsWith(e.Id + ":", StringComparison.Ordinal), e.Key));
            }
            // Before the 4.0 upgrade no row carried a 4.0 tag or a validity window: a later change that added one left a version.
            if (e.CreatedAt < Brain.Upgrade)
            {
                var then = Brain.At(e, Brain.Upgrade);
                Assert.True(!then.Tags.Any(SbTags.IsNewReserved) && then.ValidFrom is null && then.ValidUntil is null, e.Key);
            }
            // Project membership is a tag naming a project of the memory's own workspace (src/projects/registry.ts).
            foreach (var slug in e.Tags.Where(t => t.StartsWith("project:", StringComparison.Ordinal)).Select(t => t["project:".Length..]))
                Assert.Contains(Brain.Projects, p => p.Id == slug && p.WorkspaceId == e.WorkspaceId);
        });

        // A forgotten memory takes its edges to the trash, so a live edge joins two live memories; a trashed edge has the
        // trashed memory at one end.
        var live = Brain.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Brain.Edges, e => Assert.True(live.Contains(e.SourceId) && live.Contains(e.TargetId), e.Id));
        Assert.All(Brain.Trash, t => Assert.All(t.Edges, e => Assert.True(e.SourceId == t.Id || e.TargetId == t.Id, e.Id)));
        Assert.All(edges, e =>
        {
            var (from, to) = (Any(e.SourceId), Any(e.TargetId));
            Assert.Contains(e.Type, SbTags.EdgeTypes);
            Assert.Contains(e.Provenance, SbTags.Provenances);
            Assert.InRange(e.Weight, 0, 1);
            Assert.NotEqual(e.SourceId, e.TargetId);
            // A symmetric type is stored with the smaller id first (src/graph/edges.ts).
            if (e.Type == "relates_to") Assert.True(string.CompareOrdinal(e.SourceId, e.TargetId) < 0, e.Id);
            if (SbTags.EpisodicOnly.Contains(e.Type)) Assert.True(from.Kind == "episodic" && to.Kind == "episodic", $"{e.Type}: {from.Key} to {to.Key}");
            Assert.Contains(e.WorkspaceId, (string[])[from.WorkspaceId, to.WorkspaceId]);
            Assert.True(e.CreatedAt >= from.CreatedAt && e.CreatedAt >= to.CreatedAt && e.UpdatedAt >= e.CreatedAt, e.Id);
            if (e.Type == "supersedes")
            {
                // The contradiction pass closes the older fact's window where the newer one starts, and records why.
                Assert.True(e.Provenance == "system" && to.ValidUntil is { } closed && from.ValidFrom is { } opened && closed <= opened, e.Id);
                Assert.Contains(Brain.Versions, v => v.EntryId == to.Id && v.Reason == "validity" && Str(v.Meta["cause"]) == "supersede");
            }
            if (e.Type == "drawn_from") Assert.True(e.Provenance == "system" && from.Tags.Contains("auto-insight"), e.Id);
        });

        // Versions: dense from 1, after the upgrade, each a full copy of the earlier state, in the workspace the memory was in.
        Assert.All(Brain.Versions.GroupBy(v => v.EntryId), g =>
        {
            var e = Any(g.Key);
            var versions = g.OrderBy(v => v.Seq).ToArray();
            Assert.Equal(Enumerable.Range(1, versions.Length), versions.Select(v => v.Seq));
            Assert.True(versions.Zip(versions.Skip(1)).All(p => p.First.CreatedAt <= p.Second.CreatedAt), e.Key);
            Assert.All(versions, v =>
            {
                Assert.True(v.CreatedAt >= Brain.Upgrade && v.CreatedAt <= (e.UpdatedAt ?? e.CreatedAt), e.Key);
                Assert.True(v.Content is not null && v.PriorLength is null, e.Key);
                Assert.Contains(v.Reason, SbTags.VersionReasons);
                Assert.Equal(Brain.WorkspaceAt(e, v.CreatedAt), v.WorkspaceId);
                Assert.True(v.ValidFrom >= e.CreatedAt && v.ValidFrom <= v.CreatedAt, e.Key);
                Assert.Equal(Sorted(["when_at", "when_kind", "when_source", "when_label", "valid_from", "valid_until"]), Sorted(v.State.Select(p => p.Key)));
                Assert.True(v.ActorId == "" || Brain.Users.Any(u => u.Id == v.ActorId), e.Key);
                if (v.Reason == "validity") Assert.Contains(Str(v.Meta["cause"]), (string[])["supersede", "explicit", "retraction", "unretraction", "propagate"]);
            });
        });

        // Events name a known memory and member; a forget leaves one deleted event when the row entered the trash; a share
        // or unshare leaves the memory where its last move put it.
        Assert.All(Brain.Events, ev =>
        {
            var e = Any(ev.EntryId);
            Assert.Contains(ev.Event, SbTags.Events);
            Assert.True(ev.ActorId == "" || Brain.Users.Any(u => u.Id == ev.ActorId), ev.Id);
            Assert.True(ev.CreatedAt >= e.CreatedAt && ev.CreatedAt <= Brain.Now, ev.Id);
            if (ev.Event == "held") Assert.NotNull(e.HeldReason);
            if (ev.Event == "deleted") Assert.Contains(Brain.Trash, t => t.Id == ev.EntryId);
        });
        Assert.All(Brain.Trash, t =>
        {
            Assert.Single(Brain.Events, ev => ev.EntryId == t.Id && ev.Event == "deleted" && ev.CreatedAt == t.DeletedAt && ev.ActorId == t.DeletedBy);
            Assert.True(t.Entry.WorkspaceId == t.WorkspaceId && t.Entry.ActorId == t.ActorId && t.DeletedAt <= Brain.Now, t.Key);
            Assert.Contains(t.Reason, (string[])["forget", "mirror", "disconnect"]);
        });
        Assert.All(Brain.Events.Where(ev => ev.Event is "shared" or "unshared").GroupBy(ev => ev.EntryId), g =>
        {
            var last = g.MaxBy(ev => ev.CreatedAt)!;
            Assert.Equal(Any(g.Key).WorkspaceId, Str(last.Payload["workspaceId"]));
            Assert.All(g, ev => Assert.Equal(ev.Event == "shared" ? "company" : "personal", Brain.Workspace(Str(ev.Payload["workspaceId"])).Kind));
        });

        // Projects: one slug per workspace, as upstream's primary key; a project of a workspace that existed.
        Assert.Equal(Brain.Projects.Length, Brain.Projects.Select(p => (p.WorkspaceId, p.Id)).Distinct().Count());
        Assert.All(Brain.Projects, p => Assert.True(p.CreatedAt >= Brain.Workspace(p.WorkspaceId).CreatedAt
            && (p.UpdatedAt is null || p.UpdatedAt >= p.CreatedAt) && p.CreatedAt <= Brain.Now, p.Id));

        // Each export was taken by a member who had not left, under the release running then, by the time the brain describes.
        Assert.Equal(Brain.Exports.Length, Brain.Exports.Select(f => f.File).Distinct().Count());
        Assert.All(Brain.Exports, f =>
        {
            Assert.False(Brain.User(f.User).RemovedAt <= f.At, f.File);
            Assert.Equal(f.Release, Brain.Releases.Where(r => r.From <= f.At).MaxBy(r => r.From)!.Release);
            Assert.Equal(SbShape.Of(f.Release).Version, f.Version);
            Assert.True(f.At <= Brain.Now, f.File);
        });
    }

    [Fact] public void Each_export_file_is_what_upstream_would_export()
    {
        var write = Environment.GetEnvironmentVariable("RHYTHM_SB_EXPORT_WRITE");
        foreach (var file in Brain.Exports)
        {
            var derived = UpstreamExport.Take(Brain, file);
            if (!string.IsNullOrEmpty(write))
            {
                Directory.CreateDirectory(write);
                File.WriteAllText(Path.Combine(write, file.File), UpstreamExport.Write(derived));
                output.WriteLine($"wrote {Path.Combine(write, file.File)}");
                continue;
            }
            var stored = SbBrain.ReadExport(file.File);
            Assert.Equal(Keys(derived), Keys(stored));
            Assert.Equal(UpstreamExport.Canonical(derived["exported_at"]), UpstreamExport.Canonical(stored["exported_at"]));
            Assert.Equal(UpstreamExport.Canonical(derived["version"]), UpstreamExport.Canonical(stored["version"]));
            // Memories and projects in upstream's order, row by row; edges in any order, since upstream gives them none.
            foreach (var name in new[] { "entries", "projects" }.Where(derived.ContainsKey))
            {
                var (want, have) = (Rows(derived, name), Rows(stored, name));
                Assert.Equal(want.Select(r => Str(r["id"])), have.Select(r => Str(r["id"])));
                foreach (var (w, h) in want.Zip(have)) Assert.Equal(UpstreamExport.Canonical(w), UpstreamExport.Canonical(h));
            }
            Assert.Equal(Sorted(Rows(derived, "edges").Select(UpstreamExport.Canonical)), Sorted(Rows(stored, "edges").Select(UpstreamExport.Canonical)));
        }
    }

    [Fact] public void Each_export_file_has_its_releases_shape_and_nothing_more()
    {
        foreach (var file in Brain.Exports)
        {
            var shape = SbShape.Of(file.Release);
            var export = SbBrain.ReadExport(file.File);
            // No workspace, author, due date, vector id or any other column: exactly the keys this release exports.
            Assert.Equal(shape.TopKeys, Keys(export));
            Assert.True(export["ok"]!.GetValue<bool>());
            var at = Long(export["exported_at"]);
            Assert.Equal(file.At.ToUnixTimeMilliseconds(), at);
            Assert.Equal((long)shape.Version, Long(export["version"]));

            var entries = Rows(export, "entries");
            Assert.Equal(entries.Length, Ids(export).Count);
            Assert.All(entries, e =>
            {
                var id = Str(e["id"]);
                Assert.Equal(shape.EntryKeys, Keys(e));
                Assert.True(Str(e["content"]).Trim().Length > 0 && Str(e["source"]).Length > 0, id);
                Assert.All(e["tags"]!.AsArray(), t => Assert.Equal(Str(t).Trim(), Str(t)));
                var created = Long(e["created_at"]);
                Assert.InRange(created, 0, at);
                if (shape.UpdatedAt) Assert.InRange(Long(e["updated_at"]), created, at);
                foreach (var counter in new[] { "recall_count", "contradiction_wins", "contradiction_losses" }) Assert.True(Long(e[counter]) >= 0, id);
                Assert.InRange(Long(e["importance_score"]), 0, 5);
                if (shape.Validity)
                {
                    long? from = e["valid_from"] is null ? null : Long(e["valid_from"]), until = e["valid_until"] is null ? null : Long(e["valid_until"]);
                    Assert.False(from > at || until > at, id);
                    if (until is { } u) Assert.True(u >= (from ?? created), id);
                }
                // A file from before 4.0 carries none of the tags 4.0 reserved for itself.
                if (shape.Version < 3) Assert.DoesNotContain(e["tags"]!.AsArray().Select(t => Str(t)), SbTags.IsNewReserved);
            });
            Assert.Equal(entries.Select(e => Long(e["created_at"])).Order(), entries.Select(e => Long(e["created_at"])));

            Assert.All(Rows(export, "edges"), e =>
            {
                Assert.Equal(SbShape.EdgeKeys, Keys(e));
                Assert.Contains(Str(e["type"]), SbTags.EdgeTypes);
                Assert.Contains(Str(e["provenance"]), SbTags.Provenances);
                Assert.InRange(double.Parse(e["weight"]!.ToJsonString(), CultureInfo.InvariantCulture), 0, 1);
                Assert.InRange(Long(e["created_at"]), 0, at);
                Assert.NotEqual(Str(e["source_id"]), Str(e["target_id"]));
                if (Str(e["type"]) == "relates_to") Assert.True(string.CompareOrdinal(Str(e["source_id"]), Str(e["target_id"])) < 0);
            });
            var projects = Rows(export, "projects");
            Assert.All(projects, p =>
            {
                Assert.Equal(SbShape.ProjectKeys, Keys(p));
                Assert.InRange(Long(p["created_at"]), 0, at);
                Assert.Equal(p["aliases"]!.AsArray().Select(a => Str(a).Trim().ToLowerInvariant()).Distinct(), p["aliases"]!.AsArray().Select(a => Str(a)));
            });
            Assert.Equal(projects.Select(p => Long(p["created_at"])).Order(), projects.Select(p => Long(p["created_at"])));
        }
    }

    [Fact] public void Upstreams_own_import_accepts_every_row()
    {
        foreach (var file in Brain.Exports) Assert.Empty(UpstreamImportRules.Problems(SbBrain.ReadExport(file.File)));
        // The rules themselves refuse what upstream refuses.
        var bad = JsonNode.Parse("""
            { "version": 4, "entries": [ { "content": "no id" }, { "id": "x", "content": "y", "tags": "one" }, { "id": "x", "content": "y", "created_at": "today" } ],
              "edges": [ { "source_id": "a", "target_id": "a" }, { "source_id": "a", "target_id": "b", "type": "knows" }, { "source_id": "a" } ],
              "projects": [ { "id": "Bad Slug", "name": "n" }, { "id": "ok", "name": "n", "aliases": ["status:canonical"] }, { "id": "ok", "name": "n", "status": "gone" } ] }
            """);
        Assert.Equal((string[])["version must be 2 or 3", "entries[0] : missing_id", "entries[1] x: invalid_tag", "entries[2] x: invalid_created_at",
                "edges[0]: self_edge", "edges[1]: invalid_type", "edges[2]: missing_endpoint", "projects[0] Bad Slug: invalid_project: id",
                "projects[1] ok: invalid_project: aliases", "projects[2] ok: invalid_project: status"],
            UpstreamImportRules.Problems(bad));
    }

    [Fact] public void What_upstreams_own_import_would_change_is_known()
    {
        // Upstream's import keeps every row of these files, but gives an id over 40 bytes a minted id (boundedEntryId),
        // and drops the tags 4.0 writes itself from a file's tags, holding a held row again from what it read first
        // (parseEntryRow). So a restore through upstream loses decision, standing and commitment markers; the runbook has
        // Rhythm's import keep them.
        (string[] Minted, string[] Stripped) Changes(string file)
        {
            var entries = Rows(SbBrain.ReadExport(file), "entries");
            return ([.. Sorted(entries.Select(e => Str(e["id"])).Where(id => Encoding.UTF8.GetByteCount(id) > 40))],
                [.. Sorted(entries.SelectMany(e => e["tags"]!.AsArray().Select(t => Str(t))).Where(SbTags.IsNewReserved).Distinct())]);
        }
        var longId = Brain.Entry("alex-running-plan").Id;
        var (alexMinted, alexStripped) = Changes(AlexFile);
        Assert.Equal([longId], alexMinted);
        Assert.Equal(["confidence-source:stated", "confidence:0.70", "ledger:decision", "quarantine:burst", "quarantine:capsule", "quarantine:instruction",
            "standing:active"], alexStripped);
        var (samMinted, samStripped) = Changes(SamFile);
        Assert.Empty(samMinted);
        Assert.Equal(["counterparty:allotment-committee", "counterparty:maren", "owed-to-me", "quarantine:hidden", "quarantine:too_long"], samStripped);
        var (oldMinted, oldStripped) = Changes(OldFile);
        Assert.Equal([longId], oldMinted);
        Assert.Empty(oldStripped);
    }

    [Fact] public void The_brain_names_every_field_the_export_leaves_out()
    {
        var shape = SbShape.Of(Brain.Upstream.Version);
        Assert.Equal(Sorted(EntryColumns.Except(shape.EntryKeys)), Sorted(Brain.NotExported["entries"].Keys));
        Assert.Equal(Sorted(EdgeColumns.Except(SbShape.EdgeKeys)), Sorted(Brain.NotExported["edges"].Keys));
        Assert.Equal(Sorted(ProjectColumns.Except(SbShape.ProjectKeys)), Sorted(Brain.NotExported["projects"].Keys));
        // Every table the brain holds besides memories, edges and projects is named, by upstream's name, as not exported.
        Assert.Subset(Brain.NotExported["tables"].Keys.ToHashSet(), new HashSet<string> { "users", "workspaces", "memberships", "entry_versions", "entry_events", "entries_trash" });
        Assert.All(Brain.NotExported.Values.SelectMany(g => g.Values), why => Assert.False(string.IsNullOrWhiteSpace(why)));
        // And what is left out holds something in the brain, so the loss is real in these files.
        Assert.Contains(Brain.Entries, e => e.WhenAt is not null);
        Assert.Contains(Brain.Entries, e => e.StalenessCheckedAt is not null);
        Assert.NotEmpty(Brain.Versions);
        Assert.NotEmpty(Brain.Trash);
    }

    [Fact] public void The_fixture_holds_the_cases_the_import_must_handle()
    {
        var (alex, sam, old) = (SbBrain.ReadExport(AlexFile), SbBrain.ReadExport(SamFile), SbBrain.ReadExport(OldFile));
        // The sizes fixtures.md gives: memories, edges and projects in each file.
        static (int, int, int) Counts(JsonObject x) => (Rows(x, "entries").Length, Rows(x, "edges").Length, Rows(x, "projects").Length);
        Assert.Equal((29, 13, 6), Counts(alex));
        Assert.Equal((25, 11, 8), Counts(sam));
        Assert.Equal((12, 3, 0), Counts(old));

        // Scopes. Family is in both people's files under the same ids, each personal workspace in its owner's alone, and a
        // second team only in its one member's file. Family holds a share, a former member's memory and a system one.
        var (family, bees) = (Team("Family"), Team("Wickmere beekeepers"));
        var alexWs = Brain.PersonalWorkspace(Brain.Users.Single(u => u.Name == "Alex"));
        Assert.Equal(Sorted(Brain.Entries.Where(e => e.WorkspaceId == family).Select(e => e.Id)), Sorted(Ids(alex).Intersect(Ids(sam))));
        Assert.Subset(Ids(sam), Brain.Entries.Where(e => e.WorkspaceId == bees).Select(e => e.Id).ToHashSet());
        Assert.DoesNotContain(Brain.Entries, e => e.WorkspaceId == bees && Ids(alex).Contains(e.Id));
        Assert.Contains(Brain.Events, ev => ev.Event == "shared" && Any(ev.EntryId).WorkspaceId == family);
        Assert.Contains(Brain.Entries, e => e.WorkspaceId == family && e.ActorId != "" && Brain.User(e.ActorId).RemovedAt is not null);
        Assert.Contains(Brain.Entries, e => e.WorkspaceId == family && e.ActorId == "");
        // A former member's personal workspace is in no export: nobody can read it any more.
        var leftWs = Brain.PersonalWorkspace(Brain.Users.Single(u => u.RemovedAt is not null));
        Assert.Contains(Brain.Entries, e => e.WorkspaceId == leftWs);
        Assert.All(Brain.Exports, f => Assert.DoesNotContain(Ids(SbBrain.ReadExport(f.File)), id => Any(id).WorkspaceId == leftWs));

        // Links. Every edge type and provenance; an edge in Sam's file to Alex's private memory, which Sam's file does not
        // hold; a Drive file named in the text, in both address forms, and a page elsewhere.
        var current = Rows(alex, "edges").Concat(Rows(sam, "edges")).ToArray();
        Assert.Equal(Sorted(SbTags.EdgeTypes), Sorted(current.Select(e => Str(e["type"])).Distinct()));
        Assert.Equal(Sorted(SbTags.Provenances), Sorted(current.Select(e => Str(e["provenance"])).Distinct()));
        string[] Outside(JsonObject x) => [.. Rows(x, "edges").SelectMany(e => new[] { Str(e["source_id"]), Str(e["target_id"]) }).Where(id => !Ids(x).Contains(id))];
        Assert.Equal(alexWs, Any(Assert.Single(Outside(sam))).WorkspaceId);
        Assert.Empty(Outside(alex));
        Assert.Empty(Outside(old));
        var texts = Rows(alex, "entries").Concat(Rows(sam, "entries")).Select(e => Str(e["content"])).ToArray();
        var links = texts.SelectMany(t => FullUrl().Matches(t).Select(m => m.Value)).ToArray();
        Assert.Equal(2, links.Select(RelationshipDiscovery.DriveFileId).OfType<string>().Distinct().Count());
        Assert.Contains(links, l => l.StartsWith("https://docs.google.com/", StringComparison.Ordinal));
        Assert.Contains(links, l => l.StartsWith("https://drive.google.com/", StringComparison.Ordinal));
        Assert.Contains(links, l => RelationshipDiscovery.DriveFileId(l) is null && new Uri(l).Host.EndsWith(".invalid", StringComparison.Ordinal));

        // Projects: one slug from two workspaces in Sam's file, with nothing to tell them apart; an archived project; aliases.
        string[] Twice(JsonObject x) => [.. Rows(x, "projects").GroupBy(p => Str(p["id"])).Where(g => g.Count() > 1).Select(g => g.Key)];
        Assert.Equal((string[])["choir"], Twice(sam));
        Assert.Empty(Twice(alex));
        Assert.Contains(Rows(alex, "projects"), p => Str(p["status"]) == "archived");
        Assert.Contains(Rows(sam, "projects"), p => p["aliases"]!.AsArray().Count > 1);

        // Holds. Each of upstream's five hold reasons once; a row upstream serves and Rhythm's import holds (D-037); an
        // ordinary quarantine: tag from before 4.0 that SB-P8-01 decides about. Expect agrees with the tags.
        Assert.Equal(Sorted(SbTags.HoldReasons), Sorted(Brain.AllEntries.Select(e => e.HeldReason).OfType<string>()));
        Assert.Contains(Brain.Entries, e => e.Expect is { Upstream: "served", Rhythm: "held", Reason: "instruction" });
        Assert.Contains(Brain.Entries, e => e.Expect is { Rhythm: "open" } && e.Tags.Contains("quarantine:review"));
        Assert.All(Brain.Entries, e =>
        {
            Assert.Equal(e.HeldReason is not null, e.Expect?.Upstream == "held");
            if (e.Expect is not { } x) return;
            Assert.Contains(x.Upstream, (string[])["held", "served"]);
            Assert.Contains(x.Rhythm, (string[])["held", "served", "open"]);
            Assert.True(x.Rhythm == "held" ? SbTags.HoldReasons.Contains(x.Reason) : x.Reason is null, e.Key);
            if (x.Upstream == "held") Assert.Equal(e.HeldReason, x.Reason);
            Assert.False(string.IsNullOrWhiteSpace(x.Note), e.Key);
        });

        // Time and status. A replaced fact and its successor; a retracted one and a memory built on it; every status, kind
        // and volatility; due and event times; versions; two memories forgotten since the older file, which holds them.
        Assert.Contains(Brain.Edges, e => e.Type == "supersedes");
        Assert.Contains(Brain.Entries, e => e.Status == "deprecated");
        Assert.Contains(Brain.Entries, e => e.Tags.Contains("retracted-source"));
        Assert.Equal(Sorted(SbTags.Statuses), Sorted(Brain.Entries.Select(e => e.Status).OfType<string>().Distinct()));
        Assert.Equal(Sorted(SbTags.Kinds), Sorted(Brain.Entries.Select(e => e.Kind).OfType<string>().Distinct()));
        Assert.Equal(Sorted(SbTags.Volatilities), Sorted(Brain.Entries.SelectMany(e => e.Tags).Where(t => t.StartsWith("volatility:", StringComparison.Ordinal)).Select(t => t[11..]).Distinct()));
        Assert.Equal(Sorted(["due", "event"]), Sorted(Brain.Entries.Select(e => e.WhenKind).OfType<string>().Distinct()));
        Assert.True(Brain.Versions.Select(v => v.Reason).Distinct().Count() >= 3);
        Assert.Equal(2, Ids(old).Count(id => Brain.Trash.Any(t => t.Id == id)));
        Assert.DoesNotContain(Ids(alex), id => Brain.Trash.Any(t => t.Id == id));
        // The older file holds the text from before a later edit; today's file holds the edit.
        var edited = Brain.Entry("alex-contract-link");
        var before = Brain.Versions.Single(v => v.EntryId == edited.Id).Content;
        Assert.Equal(before, Str(Rows(old, "entries").Single(e => Str(e["id"]) == edited.Id)["content"]));
        Assert.Equal(edited.Content, Str(Rows(alex, "entries").Single(e => Str(e["id"]) == edited.Id)["content"]));
        Assert.NotEqual(before, edited.Content);

        // Hard text: a caller-chosen id over 40 bytes, Japanese, invisible characters, and a memory over 32 KiB.
        Assert.Contains(Ids(alex), id => Encoding.UTF8.GetByteCount(id) > 40);
        Assert.Contains(texts, t => t.Any(c => c is >= (char)0x3040 and <= (char)0x9FFF));
        Assert.Contains(texts, t => t.Contains((char)0x200B));
        Assert.Contains(texts, t => Encoding.UTF8.GetByteCount(t) > 32 * 1024);
        // Commitments, decisions and standing instructions, by their reserved tags.
        var tags = Rows(alex, "entries").Concat(Rows(sam, "entries")).SelectMany(e => e["tags"]!.AsArray().Select(t => Str(t))).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(tags, new HashSet<string> { "task", "owed-to-me", "ledger:decision", "standing:active", "capsule:core", "stale:as-of", "auto-insight" });
    }

    [Fact] public void Nothing_in_the_fixture_is_real()
    {
        Assert.StartsWith("DEMO", Brain.Label);
        Assert.Equal((string[])["Alex", "Sam", "Jo"], Brain.Users.Select(u => u.Name));
        Assert.Equal("Europe/London", Brain.Timezone);
        Assert.Equal("228cea49", Brain.Upstream.Commit);
        var files = new[] { SbBrain.File }.Concat(Brain.Exports.Select(f => f.File)).ToArray();
        var text = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(ParityCorpus.Folder, f))));
        // Invented hosts only, over http as over https, but for Drive's own address forms, which Rhythm's link finder reads,
        // with invented file ids.
        var urls = AnyUrl().Matches(text);
        Assert.NotEmpty(urls);
        Assert.All(urls, m =>
        {
            var host = new Uri(m.Value).Host;
            if (host is "docs.google.com" or "drive.google.com")
            {
                Assert.StartsWith("https://", m.Value);
                Assert.StartsWith("fx-sbx-", RelationshipDiscovery.DriveFileId(m.Value));
            }
            else Assert.EndsWith(".invalid", host);
        });
        // And no host named outside an address: with the addresses taken out, nothing that reads as a real domain is left.
        Assert.Empty(BareDomain().Matches(AnyUrl().Replace(text, " ")).Select(m => m.Value));
        Assert.All(Email().Matches(text), m => Assert.Equal("example.invalid", m.Groups["domain"].Value));
        // Nothing credential-shaped: no token, hash, password or secret field anywhere.
        Assert.DoesNotMatch(SecretKey(), text);
    }

    const string UuidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";
    [GeneratedRegex("""https://[^\s"<>']+""")] private static partial Regex FullUrl();
    [GeneratedRegex("""https?://[^\s"<>'\\]+""", RegexOptions.IgnoreCase)] private static partial Regex AnyUrl();
    // A bare host under a common public suffix (an .invalid one is not).
    [GeneratedRegex(@"\b[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|org|net|io|co|uk|app|dev|ai|me|info|edu|gov|us|de|fr|eu)\b", RegexOptions.IgnoreCase)] private static partial Regex BareDomain();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9.-]+\.[A-Za-z]+)")] private static partial Regex Email();
    [GeneratedRegex("""\"[a-z_]*(token|hash|password|secret)[a-z_]*\"\s*:""")] private static partial Regex SecretKey();
}
