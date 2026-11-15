using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;

namespace Knowledge.Tests;

[Collection(SharedStateCollection.Name)]
public sealed class SecondBrainAdapterTests : IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
    readonly FakeBrain brain = new();
    public void Dispose() => secrets.Dispose();

    SecondBrainAdapter Brain(string url = "https://brain.example.invalid/", params string[] teams)
    {
        var adapter = new SecondBrainAdapter(brain.Handler);
        adapter.Configure(new(new AdapterSettings("second-brain", new Dictionary<string, object?>
        {
            ["enabled"] = "true", ["url"] = url, ["owner"] = "alex", ["teams"] = teams.Cast<object>().ToList()
        }), TestSettings.Use(), null!, null!));
        return adapter;
    }
    static SourcePass Pass(int memories = 3000, IEnumerable<DiscoveryRecord>? previous = null) =>
        new((previous ?? []).ToDictionary(r => r.Key), (_, _, _, _) => null, memories, 1000);
    static ResourceKey Key(string id) => new("alex", "second-brain", "second-brain", id);

    [Fact] public void The_address_is_an_https_origin_with_no_default()
    {
        foreach (var bad in new[] { "", "http://brain.example.invalid/", "https://brain.example.invalid/api/", "https://brain.example.invalid:8443/",
            "https://user@brain.example.invalid/" })
            Assert.Throws<ConfigException>(() => Brain(bad));
        Assert.Throws<ConfigException>(() => Brain("https://brain.example.invalid/", "not a team"));
        Assert.Equal("second-brain", Brain().Name);
    }

    [Fact] public async Task Eligible_memories_become_stars_with_projects_links_and_Drive_citations()
    {
        var listing = await Brain(teams: FakeBrain.Team).List(Pass(), default);
        Assert.True(listing.Complete);
        var byId = listing.Records.ToDictionary(r => r.Key.ExternalId);
        // Drafts and superseded memories never become stars; projects come from tags and the registry's aliases.
        Assert.Equal([FakeBrain.Id(1), FakeBrain.Id(2), FakeBrain.Id(5), "project:personal:fitness", "project:personal:kitchen"],
            byId.Keys.Order(StringComparer.Ordinal));
        var walks = byId[FakeBrain.Id(1)];
        Assert.Equal(("memory", "Morning walks by the canal help focus", "private"), (walks.Kind, walks.Title, walks.Scope));
        Assert.Equal(walks.Title, walks.Overview);
        Assert.Equal(["second-brain:personal", "second-brain:project:personal:fitness"], walks.Groups!.Select(g => g.Id));
        Assert.Contains(walks.References, r => r.Target == Key("project:personal:fitness") && r.NativeType == "in_project" && r.Verification == "verified");
        Assert.Contains(walks.References, r => RelationshipDiscovery.DriveFileId(r.Url) == "plan");
        // Second Brain's own link, by the person: verified, directed, with its weight.
        var supports = Assert.Single(walks.References, r => r.NativeType == "supports");
        Assert.Equal((Key(FakeBrain.Id(2)), "verified", 0.8), (supports.Target, supports.Verification, supports.Strength));
        Assert.Equal("Kitchen", byId["project:personal:kitchen"].Title);
        Assert.Equal(("shared", "second-brain:team-shared"), (byId[FakeBrain.Id(5)].Scope, byId[FakeBrain.Id(5)].Groups![0].Id));
        // The personal workspace is the owner's; a team workspace is shared, so its memories are the household's.
        Assert.All(listing.Records, r => Assert.Equal(r.Scope == "shared" ? RhythmConfig.Household : "alex", r.Key.Subject));
        Assert.All(brain.Handler.Requests, r => Assert.StartsWith("GET brain.example.invalid/", r));
        RelationshipDiscovery.Validate(new(listing.Records));

        // The revision fingerprint keeps the private version's shape, so a moved install's memories do not all read as changed.
        var row = brain.Rows.Single(x => x.Id == FakeBrain.Id(2));
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(new { id = row.Id, text = row.Content, tags = row.Tags!.Order(StringComparer.Ordinal),
            created = row.Created, source = "api", Layer = "personal", Team = (string?)null });
        Assert.Equal("observed-sha256:" + Hashing.Hash(fingerprint), byId[FakeBrain.Id(2)].Revision);

        // A changed memory is a new revision; a link that changes is too.
        var revision = walks.Revision;
        brain.Links.Clear();
        Assert.NotEqual(revision, (await Brain().List(Pass(), default)).Records.Single(r => r.Key.ExternalId == FakeBrain.Id(1)).Revision);
    }

    [Fact] public async Task An_older_engines_star_account_can_be_kept()
    {
        Environment.SetEnvironmentVariable("RHYTHM_SECOND_BRAIN_STAR_ACCOUNT", "https://brain.example.invalid/users/legacy");
        try
        {
            var listing = await Brain(teams: FakeBrain.Team).List(Pass(), default);
            Assert.All(listing.Records, r => Assert.Equal("https://brain.example.invalid/users/legacy", r.Key.Account));
            Assert.All(listing.Records.SelectMany(r => r.References).Where(x => x.Target is not null),
                x => Assert.Equal("https://brain.example.invalid/users/legacy", x.Target!.Account));
        }
        finally { Environment.SetEnvironmentVariable("RHYTHM_SECOND_BRAIN_STAR_ACCOUNT", null); }
    }

    [Fact] public async Task A_failed_or_capped_listing_is_incomplete_and_removes_nothing()
    {
        brain.Down = true;
        var down = await Brain().List(Pass(), default);
        Assert.False(down.Complete);
        Assert.Empty(down.Records);
        brain.Down = false;
        Assert.False((await Brain().List(Pass(memories: 1), default)).Complete);
        // Without the link list, last pass's links are kept rather than retired.
        var first = await Brain().List(Pass(), default);
        brain.GraphDown = true;
        var kept = (await Brain().List(Pass(previous: first.Records), default)).Records.Single(r => r.Key.ExternalId == FakeBrain.Id(1));
        Assert.Contains(kept.References, r => r.NativeType == "supports");
        Assert.False(kept.ReferencesComplete);
        // No token file: the pass fails before calling the service.
        secrets.Remove("second-brain_token");
        brain.Handler.Requests.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Brain().List(Pass(), default));
        Assert.Empty(brain.Handler.Requests);
    }

    // graph.maxMemories counts every workspace together, so the owner's personal memories can use it all and leave the
    // team's (the household's) out. With scopes on each workspace lists against its own subject's share
    // (SourcePass.MemoryShares; docs/dev/memory.md section 8, Budgets), so neither crowds out the other.
    [Fact] public async Task With_scopes_on_each_workspace_lists_against_its_subjects_own_share()
    {
        static Dictionary<string, int> Memories(SourceListing l) =>
            l.Records.Where(r => r.Kind == "memory").GroupBy(r => r.Key.Subject).ToDictionary(g => g.Key, g => g.Count());
        var together = await Brain(teams: FakeBrain.Team).List(Pass(memories: 2), default);
        Assert.Equal(new Dictionary<string, int> { ["alex"] = 2 }, Memories(together));
        var shares = Pass(memories: 2) with { MemoryShares = new Dictionary<string, int> { ["alex"] = 1, ["household"] = 2 } };
        var apart = await Brain(teams: FakeBrain.Team).List(shares, default);
        Assert.Equal(new Dictionary<string, int> { ["alex"] = 1, [RhythmConfig.Household] = 1 }, Memories(apart));
        Assert.False(apart.Complete);
        // The household's team memories are the same however many the owner has.
        var none = await Brain(teams: FakeBrain.Team).List(Pass(memories: 2) with { MemoryShares = new Dictionary<string, int> { ["household"] = 2 } }, default);
        Assert.Equal(apart.Records.Where(r => r.Key.Subject == RhythmConfig.Household && r.Kind == "memory").Select(r => r.Key),
            none.Records.Where(r => r.Key.Subject == RhythmConfig.Household && r.Kind == "memory").Select(r => r.Key));
    }

    // A team memory is Family's, and its revision names only Second Brain's links within its own workspace: one the
    // service reports to a memory of the owner's personal workspace is no link here, so neither that link nor that
    // memory's being there moves the revision every Family reader is served (docs/dev/memory.md section 8).
    [Fact] public async Task A_team_memorys_revision_never_names_a_memory_of_the_personal_workspace()
    {
        async Task<DiscoveryRecord> Team() => (await Brain(teams: FakeBrain.Team).List(Pass(), default)).Records.Single(r => r.Key.ExternalId == FakeBrain.Id(5));
        var alone = await Team();
        Assert.Equal(RhythmConfig.Household, alone.Key.Subject);
        brain.Links.Add((FakeBrain.Id(5), FakeBrain.Id(1), "supports", 0.9, "explicit"));
        var linked = await Team();
        Assert.Equal(alone.Revision, linked.Revision);
        Assert.All(linked.References.Where(r => r.Target is not null), r => Assert.Equal(RhythmConfig.Household, r.Target!.Subject));
        brain.Rows.RemoveAll(r => r.Workspace == "personal");
        Assert.Equal(alone.Revision, (await Team()).Revision);
        // A link within the workspace is part of its revision, as before.
        brain.Rows.Add(new(FakeBrain.Id(6), "Team rota for the allotment.", 1_790_000_500_000, "company", FakeBrain.Team));
        brain.Links.Add((FakeBrain.Id(5), FakeBrain.Id(6), "supports", 0.9, "explicit"));
        Assert.NotEqual(alone.Revision, (await Team()).Revision);
    }

    [Fact] public async Task A_memory_is_read_from_the_service_when_its_star_opens()
    {
        var adapter = Brain();
        var records = (await adapter.List(Pass(), default)).Records.ToDictionary(r => r.Key.ExternalId);
        Assert.StartsWith("Morning walks by the canal", await adapter.ReadText(records[FakeBrain.Id(1)], default));
        // A project has no text; a memory that turned ineligible since, or another provider's record, reads as unavailable.
        Assert.Null(await adapter.ReadText(records["project:personal:fitness"], default));
        brain.Rows[1] = brain.Rows[1] with { Tags = ["quarantine:review"] };
        Assert.Null(await adapter.ReadText(records[FakeBrain.Id(2)], default));
        Assert.Null(await adapter.ReadText(records[FakeBrain.Id(1)] with { Key = new("alex", "notes", "notes", FakeBrain.Id(1)) }, default));
        brain.Down = true;
        Assert.Null(await adapter.ReadText(records[FakeBrain.Id(1)], default));
    }

    [Theory]
    [InlineData("**Groceries**: buy oats. Then more", "Groceries: buy oats")]
    [InlineData("\n# [Weekly review](https://example.invalid/r)\nbody", "Weekly review")]
    [InlineData("https://example.invalid/only-a-link", "Untitled memory")]
    public void Titles_are_a_short_label_from_the_opening_line(string text, string title) =>
        Assert.Equal(title, SecondBrainAdapter.Title(text));
}
