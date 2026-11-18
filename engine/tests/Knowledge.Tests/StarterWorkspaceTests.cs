using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Cli;
using Knowledge.Relationships;

namespace Knowledge.Tests;

[Collection(SharedStateCollection.Name)]
public class StarterWorkspaceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact]
    public async Task Default_boards_show_claims_and_ideas_only_to_their_readers_and_retries_keep_edits()
    {
        var source = fixture.Source;
        var config = MemoryToolSettings.With("tools: on, work: on, capsules: on");
        var people = await MemoryLogins.Make(source, "starter");
        await Scoped.Mode(source, "on");
        await MemoryLogins.Family(source);
        var memories = new MemoryStore(source);
        var call = MemoryLogins.Mcp;
        var boards = new DashboardStore(source);
        var feeds = new DashboardFeeds(fixture.Db, null!, AdapterRegistry.Empty, config);
        async Task Install(string template) => await StarterWorkspace.Install(source, config, template, null, TextWriter.Null, default);
        async Task<JsonArray> List() => JsonNode.Parse(await boards.List(people.App, default))!.AsArray();
        async Task<Guid> Find(string title) => Guid.Parse((string)(await List()).Single(b => (string?)b!["title"] == title)!["id"]!);
        async Task<JsonArray> Rows(Guid reader, Guid board, string name)
        {
            var definition = await boards.Feeds(reader, board, null, default);
            var data = await feeds.Read(reader, definition!.Value.Feeds, false, default);
            var feed = data["feeds"]![name]!;
            Assert.Equal("ok", (string?)feed["status"]);
            return feed["memories"]!.AsArray();
        }

        await Task.WhenAll(Install("workspace"), Install("workspace"));
        var projects = await Find("Projects");
        var agents = await Find("Agents");
        Assert.DoesNotContain(await List(), b => (string?)b!["title"] == "Groceries");
        Assert.Empty(await Rows(people.Sam, projects, "work"));
        var work = (await memories.CreateWork(people.Alex, new("Fix the shed latch", Scope: "shared", Project: "first-project"),
            "task", "ready", null, null, call, default)).Write.Id;
        var held = await memories.ClaimWork(people.Alex, work, null, "Shed session - agent", null, call, default);
        Assert.Equal("Shed session - agent", (string?)(await Rows(people.Sam, agents, "work")).Single()!["work"]!["claim"]!["label"]);
        await memories.UpdateWork(people.Alex, work, null, "review", null, false, null, null, call, default, claim: held.Claimed!.Id);
        Assert.Equal("review", (string?)(await Rows(people.Sam, agents, "work")).Single()!["work"]!["state"]);
        await memories.UpdateWork(people.Alex, work, null, "done", null, false, "Verified the latch.", null, call, default, claim: held.Claimed.Id);
        Assert.Empty(await Rows(people.Sam, agents, "work"));
        Assert.Equal("done", (string?)(await Rows(people.Sam, projects, "work")).Single()!["work"]!["state"]);

        await memories.Create(people.Alex, new("Shared idea", ["project-idea"], Scope: "shared", Project: "project-ideas"), call, default);
        await memories.Create(people.Alex, new("Private idea", ["project-idea"], Scope: "private"), call, default);
        Assert.Equal(2, (await Rows(people.Alex, projects, "ideas")).Count);
        Assert.Equal("Shared idea", (string?)(await Rows(people.Sam, projects, "ideas")).Single()!["preview"]);
        var log = (await Rows(people.Sam, agents, "handoffs")).Single(row => (string?)row!["preview"] == "ACTIVITY log of coding agents")!;
        await memories.Append(people.Alex, Guid.Parse((string)log["id"]!), "Shed session - agent | verified the latch", null, call, default);
        await memories.Create(people.Alex, new("OPEN handoff: inspect the hinges", ["handoff"], Scope: "shared", Project: "agent-handoffs"), call, default);
        Assert.Contains(await Rows(people.Sam, agents, "handoffs"), row => ((string)row!["preview"]!).Contains("verified the latch"));

        var capsules = new PromptCapsules(source);
        var capsule = await capsules.Build(people.Sam, new("project", "first-project", "shared"), default);
        Assert.Contains("open-questions", capsule.Body);
        var core = await capsules.Build(people.Sam, new("core", Scope: "shared"), default);
        Assert.Contains("principles", core.Body);
        var editor = await TerminalLogin.For(source, config.FirstPerson, default);
        var draft = new DashboardDraft("Renamed by owner", "Owner edit", "<p>Kept</p>", [new("work","memories",null!)], "Rename");
        await boards.Save(editor, projects, null, draft, [], draft.Note, default);
        await Install("workspace");
        Assert.Equal(projects, await Find("Renamed by owner"));
        Assert.DoesNotContain(await List(), b => (string?)b!["title"] == "Projects");
        await Task.WhenAll(Install("groceries"), Install("groceries"));
        Assert.NotEqual(Guid.Empty, await Find("Groceries"));
    }
}
