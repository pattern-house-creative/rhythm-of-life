using System.Text.Json;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The link vocabulary (SB-P4-01, D-030, D-015; LinkTypes, migration 051): Second Brain's eight edge types beside Rhythm's
// own, the direction each has, the episodic rule, and upstream's provenance as Rhythm's verification; discovered per type,
// for the links Rhythm states itself (its own records' references and its suggestions) and for a source's own (kept as the
// source states them). Fictional people only: alex and the household; every text is DEMO.
public sealed class LinkTypeTests
{
    static readonly string[] Upstream = ["relates_to", "supersedes", "caused_by", "decided", "about_person", "part_of_project", "follows", "drawn_from"];
    static readonly string[] Rhythms = ["references", "in_project", "in_folder", "about", "similar_to", "copy_of", "contains", "depends_on"];
    public static TheoryData<string> Types() => [.. LinkTypes.All.Select(t => t.Name)];
    static readonly DateTimeOffset Then = DateTimeOffset.UtcNow.AddMinutes(-5);

    // A native memory's star, as MemoryStars lists one, of `kind` (null: not classified).
    static DiscoveryRecord Memory(string? kind, string subject = "household", params Reference[] references) =>
        new(MemoryStars.Key(subject, Guid.NewGuid()), subject == "household" ? "shared" : "private", "memory", "DEMO: a fictional memory",
            null, "1", Then, Then, references, MemoryKind: kind);
    // A record a source listed itself (a note of the notes adapter): its links are the source's own.
    static DiscoveryRecord Note(params Reference[] references) =>
        SyntheticRelationshipSource.New("notes", "note", "notes") with { References = references };
    static Reference Typed(ResourceKey target, string type, bool directed, string verification = "verified") =>
        new(target, null, "demo:typed-link", "DEMO typed link", type, true, verification, 0.9, directed);

    [Fact] public void The_vocabulary_lists_upstreams_eight_types_beside_Rhythms_own()
    {
        Assert.Equal(Upstream, LinkTypes.All.Where(t => t.Upstream).Select(t => t.Name));
        Assert.Equal(Rhythms, LinkTypes.All.Where(t => !t.Upstream).Select(t => t.Name));
        // Of upstream's, only relates_to has no direction, and only decided and follows need kinds: episodic at both ends.
        Assert.Equal(["relates_to"], LinkTypes.All.Where(t => t.Upstream && !t.Directed).Select(t => t.Name));
        Assert.Equal(["decided", "follows"], LinkTypes.All.Where(t => t.Kinds is not null).Select(t => t.Name));
        Assert.All(LinkTypes.All.Where(t => t.Kinds is not null), t => Assert.Equal([LinkTypes.Episodic], t.Kinds!));
        // Rhythm's own keep the direction their makers give them today (a topic, a likeness and a copy have none).
        Assert.Equal(["about", "similar_to", "copy_of"], LinkTypes.All.Where(t => !t.Upstream && !t.Directed).Select(t => t.Name));
        Assert.Equal((SimilaritySuggestions.Type, SimilaritySuggestions.CopyType), ("similar_to", "copy_of"));
        // Each name has the map's shape, and none is listed twice.
        Assert.All(LinkTypes.All, t => Assert.Matches("^[a-z][a-z0-9_-]{0,63}$", t.Name));
        Assert.Equal(LinkTypes.All.Count, LinkTypes.All.Select(t => t.Name).Distinct().Count());
        // A type not listed is a source's own word, with the direction its source states.
        Assert.Null(LinkTypes.Find("supports"));
        Assert.True(LinkTypes.Directed("supports", true));
        Assert.False(LinkTypes.Directed("supports", false));
        Assert.False(LinkTypes.Directed("relates_to", true));
        Assert.True(LinkTypes.Directed("caused_by", false));
    }

    [Fact] public void The_episodic_rule_joins_only_two_episodic_memories()
    {
        string?[] kinds = [LinkTypes.Episodic, LinkTypes.Semantic, null];
        foreach (var type in LinkTypes.All.Select(t => t.Name).Append("supports"))
            foreach (var a in kinds)
                foreach (var b in kinds)
                    Assert.Equal(type is "decided" or "follows" ? a == LinkTypes.Episodic && b == LinkTypes.Episodic : true,
                        LinkTypes.Allows(type, a, b));
    }

    [Fact] public void Upstreams_provenance_is_Rhythms_verification_and_back()
    {
        Assert.Equal(("verified", "inferred", "suggested", "suggested", "suggested"), (LinkTypes.Verification("explicit"),
            LinkTypes.Verification("system"), LinkTypes.Verification("inferred"), LinkTypes.Verification(null), LinkTypes.Verification("guessed")));
        foreach (var provenance in new[] { "explicit", "inferred", "system" })
            Assert.Equal(provenance, LinkTypes.Provenance(LinkTypes.Verification(provenance)));
        Assert.Throws<ArgumentException>(() => LinkTypes.Provenance("disputed"));
    }

    // A link one of Rhythm's own records states: the vocabulary's direction whatever the reference says, and a type that
    // needs kinds only between two memories of those kinds.
    [Theory, MemberData(nameof(Types))] public void A_link_Rhythm_states_is_found_with_its_types_direction_and_rule(string type)
    {
        var listed = LinkTypes.Find(type)!;
        foreach (var (from, to) in new[] { (LinkTypes.Episodic, LinkTypes.Episodic), (LinkTypes.Episodic, LinkTypes.Semantic),
            (LinkTypes.Semantic, LinkTypes.Semantic), ((string?)null, LinkTypes.Episodic), (LinkTypes.Episodic, (string?)null) })
        {
            var target = Memory(to);
            var source = Memory(from, references: Typed(target.Key, type, !listed.Directed));
            var links = RelationshipDiscovery.Discover([source, target]);
            if (!LinkTypes.Allows(type, from, to)) { Assert.Empty(links); continue; }
            var link = Assert.Single(links);
            Assert.Equal((source.Key, target.Key, type, type, "verified", "native", 0.9, listed.Directed),
                (link.Source, link.Target, link.Type, link.NativeType, link.Verification, link.Method, link.Strength, link.Directed));
        }
        // A memory and a project's star (no memory, so no kind): a type that needs kinds never joins them.
        var project = Memory(null) with { Key = MemoryStars.ProjectKey("household", Guid.NewGuid()), Kind = "project", MemoryKind = null };
        var member = Memory(LinkTypes.Episodic, references: Typed(project.Key, type, listed.Directed));
        Assert.Equal(listed.Kinds is null ? 1 : 0, RelationshipDiscovery.Discover([member, project]).Count);
        // A catalog star's link is Rhythm's own too: its type's direction, whatever it states, and no type that needs kinds
        // (a catalog star has none).
        var memory = Memory(LinkTypes.Episodic);
        var star = new DiscoveryRecord(new ResourceKey("household", CatalogStars.Provider, CatalogStars.DataAccount("demo"), "household:demo-group:shared"),
            "shared", "record", "DEMO: a fictional catalog star", null, "1", Then, Then, [Typed(memory.Key, type, !listed.Directed)]);
        var found = RelationshipDiscovery.Discover([star, memory]);
        if (listed.Kinds is not null) Assert.Empty(found);
        else Assert.Equal(listed.Directed, Assert.Single(found).Directed);
    }

    // A source's own link (here a note's) is the source's assertion: kept as stated, its type, direction and verification,
    // whatever kinds its ends have, since Rhythm is not sent them. Existing links keep their names and meaning.
    [Theory, MemberData(nameof(Types))] public void A_sources_own_link_is_found_as_the_source_states_it(string type)
    {
        foreach (var directed in new[] { true, false })
        {
            var target = Note();
            var source = Note(Typed(target.Key, type, directed, "inferred"));
            var link = Assert.Single(RelationshipDiscovery.Discover([source, target]));
            Assert.Equal((type, "inferred", directed), (link.Type, link.Verification, link.Directed));
        }
    }

    // A suggestion is Rhythm's own too: the vocabulary's direction, and the episodic rule by its ends' kinds.
    [Theory, MemberData(nameof(Types))] public void A_suggestion_keeps_its_types_direction_and_rule(string type)
    {
        var listed = LinkTypes.Find(type)!;
        foreach (var kind in new[] { LinkTypes.Episodic, LinkTypes.Semantic })
        {
            var (a, b) = (Memory(kind), Memory(kind));
            var links = RelationshipDiscovery.Discover([a, b],
                [new(a.Key, b.Key, type, "demo:suggestion", "DEMO alike", "demo-suggester", 0.7, !listed.Directed)]);
            if (!LinkTypes.Allows(type, kind, kind)) { Assert.Empty(links); continue; }
            var link = Assert.Single(links);
            Assert.Equal((type, "suggested", listed.Directed, 0.7), (link.Type, link.Verification, link.Directed, link.Strength));
        }
        // Between two notes, no kinds: a type that needs them is never suggested.
        var (x, y) = (Note(), Note());
        Assert.Equal(listed.Kinds is null ? 1 : 0, RelationshipDiscovery.Discover([x, y],
            [new(x.Key, y.Key, type, "demo:suggestion", "DEMO alike", "demo-suggester")]).Count);
    }

    [Fact] public void A_type_not_listed_keeps_its_stated_direction_from_any_record()
    {
        foreach (var directed in new[] { true, false })
        {
            var target = Memory(null);
            var link = Assert.Single(RelationshipDiscovery.Discover([Memory(null, references: Typed(target.Key, "supports", directed)), target]));
            Assert.Equal(("supports", directed), (link.Type, link.Directed));
        }
    }

    // Only a native memory's star carries a kind, and only a kind there is; every other star's snapshot is as before.
    [Fact] public void Only_a_native_memorys_star_carries_its_kind()
    {
        RelationshipDiscovery.Validate(new([Memory(LinkTypes.Episodic), Memory(LinkTypes.Semantic), Memory(null)]));
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Validate(new([Memory("procedural")])));
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Validate(new([Note() with { MemoryKind = LinkTypes.Episodic }])));
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Validate(new([Memory(LinkTypes.Episodic) with { Kind = "project" }])));
        Assert.DoesNotContain("MemoryKind", JsonSerializer.Serialize(Note()));
        Assert.DoesNotContain("MemoryKind", JsonSerializer.Serialize(Memory(null)));
        Assert.Contains("\"MemoryKind\":\"episodic\"", JsonSerializer.Serialize(Memory(LinkTypes.Episodic)));
    }

    [Fact] public async Task A_native_memorys_star_carries_the_memorys_kind()
    {
        static CatalogMemory Catalogued(int i, string? kind) => new(new Guid($"00000000-0000-4000-8000-{i:D12}"), "household", "shared",
            $"DEMO: fictional memory {i}.", [], "draft", null, 1, 1, Then.AddMinutes(-i), Then.AddMinutes(-i), kind);
        var principal = Guid.NewGuid();
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), principal, Path.GetTempPath());
        var source = new CatalogRelationshipSource(settings, AdapterRegistry.Empty, (_, _) => Task.FromResult(new CatalogInventory([], [],
            Memories: [Catalogued(1, "episodic"), Catalogued(2, "semantic"), Catalogued(3, null)], Projects: [])));
        var batch = await source.Fetch(principal, default);
        RelationshipDiscovery.Validate(batch);
        var kinds = batch.Records.Where(r => r.Key.Provider == MemoryStars.Provider).ToDictionary(r => r.Key.ExternalId, r => r.MemoryKind);
        Assert.Equal(("episodic", "semantic", (string?)null), (kinds[Catalogued(1, null).Id.ToString()], kinds[Catalogued(2, null).Id.ToString()],
            kinds[Catalogued(3, null).Id.ToString()]));
    }
}

// Second Brain's own links, one per upstream type and with each provenance, as the adapter lists them and discovery finds
// them: their type, upstream's direction, the verification their provenance maps to, their weight. A type that needs kinds
// is kept as Second Brain states it, as every source's own link is.
[Collection(SharedStateCollection.Name)]
public sealed class SecondBrainLinkTypeTests : IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
    readonly FakeBrain brain = new();
    public void Dispose() => secrets.Dispose();

    [Fact] public async Task Each_upstream_type_is_found_with_its_direction_and_its_provenance_as_a_verification()
    {
        var adapter = new SecondBrainAdapter(brain.Handler);
        adapter.Configure(new(new AdapterSettings("second-brain", new Dictionary<string, object?>
        {
            ["enabled"] = "true", ["url"] = "https://brain.example.invalid/", ["owner"] = "alex", ["teams"] = new List<object>()
        }), TestSettings.Use(), null!, null!));
        string?[] provenances = ["explicit", "system", "inferred", null];
        var upstream = LinkTypes.All.Where(t => t.Upstream).ToArray();
        brain.Links.Clear();
        for (var i = 0; i < upstream.Length; i++) brain.Links.Add((FakeBrain.Id(1), FakeBrain.Id(2), upstream[i].Name, 0.7, provenances[i % 4]!));
        var listing = await adapter.List(new(new Dictionary<ResourceKey, DiscoveryRecord>(), (_, _, _, _) => null, 3000, 1000), default);
        var links = RelationshipDiscovery.Discover(listing.Records)
            .Where(l => l.Source.ExternalId == FakeBrain.Id(1) && l.Target.ExternalId == FakeBrain.Id(2)).ToDictionary(l => l.Type);
        Assert.Equal(upstream.Select(t => t.Name).Order(StringComparer.Ordinal), links.Keys.Order(StringComparer.Ordinal));
        for (var i = 0; i < upstream.Length; i++)
        {
            var link = links[upstream[i].Name];
            Assert.Equal((FakeBrain.Id(1), FakeBrain.Id(2), LinkTypes.Verification(provenances[i % 4]), upstream[i].Directed, 0.7),
                (link.Source.ExternalId, link.Target.ExternalId, link.Verification, link.Directed, link.Strength));
        }
        Assert.False(links["relates_to"].Directed);
        Assert.Equal(("verified", "inferred", "suggested", "suggested"),
            (links["relates_to"].Verification, links["supersedes"].Verification, links["caused_by"].Verification, links["decided"].Verification));
    }
}
