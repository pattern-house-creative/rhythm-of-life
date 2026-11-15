using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// Migration 051 (SB-P4-01): the database's link vocabulary is the engine's (LinkTypes), the episodic rule answers the
// same in both, a person- or AI-made link's confidence, source revision and checked time are checked, kept in the trash
// and brought back by a restore, and carried by rhythm export and rhythm import; a native memory's kind reaches the map's
// read of the memory tables. Fictional people only: alex, sam and the household; every text is DEMO.
public sealed class LinkTypeDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    [PostgresFact] public async Task The_databases_vocabulary_and_episodic_rule_are_the_engines()
    {
        var listed = new List<(string, bool, string, bool)>();
        await using (var c = await fixture.Source.OpenConnectionAsync())
        await using (var cmd = Database.Command(c, null, """
            SELECT t.name,t.directed,coalesce(array_to_string(t.kinds,','),''),t.upstream
            FROM link_types() WITH ORDINALITY AS t(name,directed,kinds,upstream,n) ORDER BY t.n
            """))
        await using (var row = await cmd.ExecuteReaderAsync())
            while (await row.ReadAsync()) listed.Add((row.GetString(0), row.GetBoolean(1), row.GetString(2), row.GetBoolean(3)));
        Assert.Equal(LinkTypes.All.Select(t => (t.Name, t.Directed, string.Join(",", t.Kinds ?? []), t.Upstream)), listed);
        string?[] kinds = [LinkTypes.Episodic, LinkTypes.Semantic, null];
        foreach (var type in LinkTypes.All.Select(t => t.Name).Append("supports"))
            foreach (var a in kinds)
                foreach (var b in kinds)
                    Assert.Equal(LinkTypes.Allows(type, a, b), (bool)(await MemoryRows.Scalar(fixture.Source,
                        "SELECT link_kinds_allow(@t,@a,@b)", ("t", type), ("a", (object?)a ?? DBNull.Value), ("b", (object?)b ?? DBNull.Value)))!);
    }

    [PostgresFact] public async Task A_made_links_confidence_source_revision_and_checked_time_are_checked_and_come_back_with_a_restore()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "typed-links");
        await Scoped.Mode(s, "off");
        var plan = (await Store.Create(who.Alex, new("DEMO: a fictional plan for the allotment.", Kind: "episodic"), MemoryLogins.Mcp, default)).Id;
        var budget = (await Store.Create(who.Alex, new("DEMO: its fictional budget.", Kind: "episodic"), MemoryLogins.Mcp, default)).Id;
        // Out of range, or no revision at all: refused by the table, as a writer's mistake is.
        foreach (var (column, value) in new (string, object)[] { ("confidence", 1.5), ("confidence", -0.1), ("confidence", double.NaN),
            ("source_revision", ""), ("source_revision", new string('r', 501)) })
            Assert.Equal("23514", await MemoryRows.Fails(s, $"""
                INSERT INTO made_links(link_id,source_id,target_id,type,made_by,{column}) VALUES(gen_random_uuid(),@a,@b,'follows',@p,@v)
                """, ("a", plan), ("b", budget), ("p", who.Alex), ("v", value)));
        var link = Guid.NewGuid();
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,confidence,source_revision,checked_at)
            VALUES(@l,@a,@b,'follows','inferred','DEMO: written the same morning',@p,0.82,'memory:3','2026-11-01T09:00:00Z')
            """, ("l", link), ("a", plan), ("b", budget), ("p", who.Alex));
        const string Kept = "SELECT concat_ws('|',type,verification,confidence,source_revision,checked_at AT TIME ZONE 'UTC') FROM made_links WHERE link_id=@l";
        var before = (string)(await MemoryRows.Scalar(s, Kept, ("l", link)))!;
        Assert.Equal("follows|inferred|0.82|memory:3|2026-11-01 09:00:00", before);
        // Forgotten, the link waits in the trash row whole; restored, it comes back as it was.
        var forgot = await Store.Forget(who.Alex, plan, null, MemoryLogins.Mcp, default);
        Assert.Equal(1, forgot.Trash.Links);
        Assert.Null(await MemoryRows.Scalar(s, Kept, ("l", link)));
        var back = await Store.Restore(who.Alex, plan, forgot.Trash.Nonce, MemoryLogins.Mcp, default);
        Assert.Equal((1, 0), (back.Links, back.LinksGone));
        Assert.Equal(before, await MemoryRows.Scalar(s, Kept, ("l", link)));
        // A link kept before 051 (its trash row has none of the three) comes back with none.
        var old = Guid.NewGuid();
        await MemoryRows.Run(s, "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(@l,@b,@a,'relates_to',@p)",
            ("l", old), ("a", plan), ("b", budget), ("p", who.Alex));
        var again = await Store.Forget(who.Alex, budget, null, MemoryLogins.Mcp, default);
        await MemoryRows.Run(s, """
            UPDATE memory_trash SET links=(SELECT jsonb_agg(e-'confidence'-'source_revision'-'checked_at') FROM jsonb_array_elements(links) e)
            WHERE memory_id=@m
            """, ("m", budget));
        var restored = await Store.Restore(who.Alex, budget, again.Trash.Nonce, MemoryLogins.Mcp, default);
        Assert.Equal((2, 0), (restored.Links, restored.LinksGone));
        Assert.Equal("relates_to|verified", await MemoryRows.Scalar(s, Kept, ("l", old)));
        Assert.Equal("follows|inferred", await MemoryRows.Scalar(s, Kept, ("l", link)));
    }

    [PostgresFact] public async Task The_maps_read_of_the_memory_tables_carries_each_memorys_kind()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "link-kinds");
        await Scoped.Mode(s, "off");
        async Task<Guid> Memory(string text, string? kind) => (await Store.Create(who.Alex, new(text, Kind: kind), MemoryLogins.Mcp, default)).Id;
        var (episodic, semantic, none) = (await Memory("DEMO: the fictional walk this morning.", "episodic"),
            await Memory("DEMO: the fictional towpath is two miles long.", "semantic"), await Memory("DEMO: a fictional note.", null));
        var system = await new Principals(s).System(Principals.GraphName, default);
        var inventory = await CatalogReader.Inventory(fixture.Db, system, RelationshipCatalog.For(TestSettings.Use()) with { MemoryBudget = 3000 }, default);
        var kinds = inventory.Memories!.ToDictionary(m => m.Id, m => m.Kind);
        Assert.Equal(("episodic", "semantic", (string?)null), (kinds[episodic], kinds[semantic], kinds[none]));
    }
}

// rhythm export and rhythm import carry a made link's confidence, source revision and checked time (051) when it has
// them, a live link's and one a trashed memory keeps alike, and refuse one of another shape; a link with none is the line
// it was (MemoryArchiveTests' byte-for-byte checks).
public sealed class LinkTypeArchiveTests
{
    [PostgresFact] public async Task An_export_and_an_import_carry_a_links_confidence_source_revision_and_checked_time()
    {
        await using var a = await MemoryArchiveTests.Install.Make();
        var s = await MemoryArchiveTests.Seed(a);
        await a.Run("UPDATE made_links SET confidence=0.82,source_revision='memory:3',checked_at='2026-11-01T09:00:00Z' WHERE link_id=@l",
            ("l", s.WalkCafe));
        await a.Run("""
            UPDATE memory_trash SET links=(SELECT jsonb_agg(CASE WHEN e->>'link_id'=@l
                THEN e||jsonb_build_object('confidence',0.4,'source_revision','memory:2','checked_at','2026-10-30T08:00:00+00:00') ELSE e END)
              FROM jsonb_array_elements(links) e) WHERE memory_id=@m
            """, ("l", s.WalkGone.ToString()), ("m", s.Gone));
        var file = await a.Export("alex");
        string Line(string f, Guid id) => f.Split('\n').Single(l => l.Contains($"\"id\": \"{id}\"") && l.Contains("\"verification\""));
        Assert.Contains("\"confidence\": 0.82", Line(file, s.WalkCafe));
        Assert.Contains("\"sourceRevision\": \"memory:3\"", Line(file, s.WalkCafe));
        Assert.Contains("\"checkedAt\": \"2026-11-01T09:00:00+00:00\"", Line(file, s.WalkCafe));
        Assert.Contains("\"confidence\": 0.4", Line(file, s.WalkGone));
        Assert.DoesNotContain("confidence", Line(file, s.TeaBins));

        await using var b = await MemoryArchiveTests.Install.Make();
        await b.Run("UPDATE memberships SET role='admin' WHERE person_id='alex'");
        // Of another shape, refused as invalid: a confidence past 1 (the table refuses it), a source revision of nothing (as the
        // table would, before it waits in a trash row), a confidence as text.
        var bad = file.Replace(Line(file, s.WalkCafe), Line(file, s.WalkCafe).Replace("\"confidence\": 0.82", "\"confidence\": 1.5"))
            .Replace(Line(file, s.WalkGone), Line(file, s.WalkGone).Replace("\"sourceRevision\": \"memory:2\"", "\"sourceRevision\": \"\""));
        var refused = await b.Import(bad, "alex", family: true);
        Assert.Equal((2, 1), (refused.Count("link", "refused:invalid"), refused.Count("link", "added")));
        // The kept link comes in with this one (its memory is here from the import before); the live one, its confidence as
        // text, does not.
        var text = file.Replace(Line(file, s.WalkCafe), Line(file, s.WalkCafe).Replace("\"confidence\": 0.82", "\"confidence\": \"0.82\""));
        var asText = await b.Import(text, "alex", family: true);
        Assert.Equal((1, 1, 1), (asText.Count("link", "refused:invalid"), asText.Count("link", "added"), asText.Count("link", "present")));
        Assert.Equal(0L, await b.Scalar("SELECT count(*) FROM made_links WHERE link_id=@l", ("l", s.WalkCafe)));

        // As exported: the live one comes in with the three, and the kept one has them once its memory is restored.
        var report = await b.Import(file, "alex", family: true);
        Assert.Equal((1, 2), (report.Count("link", "added"), report.Count("link", "present")));
        const string Kept = "SELECT concat_ws('|',confidence,source_revision,checked_at AT TIME ZONE 'UTC',imported) FROM made_links WHERE link_id=@l";
        Assert.Equal("0.82|memory:3|2026-11-01 09:00:00|t", await b.Scalar(Kept, ("l", s.WalkCafe)));
        var nonce = (Guid)(await b.Scalar("SELECT nonce FROM memory_trash WHERE memory_id=@id", ("id", s.Gone)))!;
        var back = await b.Store.Restore(b.Who.Alex, s.Gone, nonce, MemoryLogins.Mcp, default);
        Assert.Equal((1, 0), (back.Links, back.LinksGone));
        Assert.Equal("0.4|memory:2|2026-10-30 08:00:00|t", await b.Scalar(Kept, ("l", s.WalkGone)));
        Assert.Equal("t", await b.Scalar(Kept, ("l", s.TeaBins)));
    }
}
