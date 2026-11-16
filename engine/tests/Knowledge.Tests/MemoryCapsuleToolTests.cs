using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Knowledge.Tests;

// set_capsule (migration 078): an AI client's own step into a capsule slot, only with memory.capsuleWrites on beside
// memory.tools and memory.capsules. Off, absent or with either of the others off, nothing is made and every description
// is as before; on, it adds the one tool and says so where get_prompt_capsule and update named a person's step.
public sealed class MemoryCapsuleToolTests
{
    static readonly RhythmConfig All = MemoryToolSettings.With("tools: on, capsules: on, capsuleWrites: on");
    static readonly RhythmConfig Before = MemoryToolSettings.With("tools: on, capsules: on");

    [Fact] public void The_tool_is_made_and_listed_only_with_tools_capsules_and_capsule_writes_on()
    {
        static string[] Made(RhythmConfig config) => [.. HarnessTools.Create(config, []).Select(t => t.ProtocolTool.Name)];
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.On, Before, MemoryToolSettings.With("capsuleWrites: on"),
            MemoryToolSettings.With("tools: on, capsuleWrites: on"), MemoryToolSettings.With("capsules: on, capsuleWrites: on"),
            MemoryToolSettings.With("tools: on, capsules: on, capsuleWrites: off"), MemoryToolSettings.With("tools: on, capsules: on, capsuleWrites: null") })
        {
            Assert.False(MemorySettings.CapsuleWritesOn(config));
            Assert.DoesNotContain(MemoryTools.CapsuleWriteTool, Made(config));
        }
        Assert.True(MemorySettings.CapsuleWritesOn(All));
        Assert.Equal(Made(Before).Append(MemoryTools.CapsuleWriteTool).Order(), Made(All).Order());
        Assert.Throws<ConfigException>(() => MemorySettings.From(MemoryToolSettings.With("capsuleWrites: yes")));
        // It changes two descriptions and no other: who sets a slot, as get_prompt_capsule and update say it.
        var all = HarnessTools.Create(All, []).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
        foreach (var tool in HarnessTools.Create(Before, []).Select(t => t.ProtocolTool).Where(t => t.Name is not ("get_prompt_capsule" or "update")))
            Assert.Equal(tool.Description, all[tool.Name].Description);
        Assert.Contains("Only a person puts a memory into a slot", ToolText.Describe("get_prompt_capsule", Before));
        Assert.DoesNotContain("set_capsule", ToolText.Describe("get_prompt_capsule", Before));
        Assert.DoesNotContain("Only a person puts a memory into a slot", all["get_prompt_capsule"].Description);
        Assert.Contains("set_capsule puts a memory into a slot or takes it out, only when the person asks you to", all["get_prompt_capsule"].Description);
        Assert.Contains("A capsule slot is a person's step", ToolText.Describe("update", Before));
        Assert.Contains("A capsule slot is set with set_capsule", all["update"].Description);
        var mine = all[MemoryTools.CapsuleWriteTool];
        Assert.Contains("Test install", mine.Description);
        Assert.Contains("only when the person asks you to in this conversation", mine.Description);
        // Its hints and its listed schema: a write, not destructive, idempotent, answering a write's shape.
        Assert.Equal((false, false, true, false), (mine.Annotations!.ReadOnlyHint, mine.Annotations.DestructiveHint, mine.Annotations.IdempotentHint,
            mine.Annotations.OpenWorldHint));
        Assert.Equal(["capsule", "expected_revision", "id", "slot"], mine.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(MemoryTools.OutputSchema("remember").GetRawText(), mine.OutputSchema!.Value.GetRawText());
        Assert.True(MemoryTools.Answers(MemoryTools.CapsuleWriteTool));
        Assert.DoesNotContain(MemoryTools.CapsuleWriteTool, MemoryTools.Names);
        // tools/list leaves it out unless all three are on, whatever the server was made with.
        var made = new ListToolsResult { Tools = [.. HarnessTools.Create(All, []).Select(t => t.ProtocolTool)] };
        var services = new ServiceCollection().BuildServiceProvider();
        Assert.Contains(HarnessTools.Filter(new() { Tools = [.. made.Tools] }, services, All).Tools, t => t.Name == MemoryTools.CapsuleWriteTool);
        Assert.DoesNotContain(HarnessTools.Filter(new() { Tools = [.. made.Tools] }, services, Before).Tools, t => t.Name == MemoryTools.CapsuleWriteTool);
        // A capsule change is listed among a client's changes, so a person sees it and can undo it.
        Assert.Contains("capsule_changed", MemoryStore.Undoable);
        Assert.Equal("capsule", MemoryStore.Family("capsule_changed"));
    }
}

public sealed class MemoryCapsuleToolDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly RhythmConfig All = MemoryToolSettings.With("tools: on, capsules: on, capsuleWrites: on");
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    static readonly MemoryCall Cli = new("cli", "DEMO terminal");

    async Task<MemoryToolClient> As(Guid login, RhythmConfig? config = null, string name = "DEMO client")
    {
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(login)).AddSingleton(fixture.Db)
            .AddSingleton(config ?? All).AddSingleton(new Retrieval(fixture.Db)).BuildServiceProvider();
        return await MemoryToolClient.Start(services, config ?? All, name);
    }

    static Guid Id(JsonNode answer) => Guid.Parse((string)answer["id"]!);
    static async Task<string> Refused(Func<Task> act) => (await Assert.ThrowsAsync<MemoryRefused>(act)).Code;

    Task<object?> Slot(Guid id) => MemoryLogins.Scalar(fixture.Source,
        "SELECT coalesce(capsule,'-')||'|'||coalesce(capsule_slot,'-')||'|'||revision FROM memories WHERE id=@id", ("id", id));

    // With the setting on an AI client puts its memory into a slot through the tool, as mcp and under its own name: a
    // change with a version and an audit row, never a second memory into a slot one fills, and reversed by setting the
    // slot again. Its own undo of a slot stays refused; a person's undoes it. Every other way in stays closed.
    [PostgresFact] public async Task An_AI_client_sets_and_clears_a_slot_through_the_tool_and_a_person_undoes_it()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "capsule-tool");
        var at = await MemoryLogins.Terminals(s);
        var store = new MemoryStore(s);
        await using var alex = await As(who.Alex);

        var quiet = Id(await alex.Ok("remember", new { content = "DEMO: Alex prefers quiet mornings.", project = "capsule-tool-home" }));
        // A draft takes its slot and waits: the answer says the capsule serves it once confirmed.
        var set = await alex.Ok("set_capsule", new { id = quiet, capsule = "Core", slot = " Preferences ", expected_revision = 1 });
        Assert.Equal((2, false, "draft"), ((int)set["revision"]!, (bool)set["unchanged"]!, (string)set["status"]!));
        Assert.Equal(["capsule"], set["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Contains("Put into the core capsule's preferences slot", (string)set["message"]!);
        Assert.Contains("once it is confirmed", (string)set["message"]!);
        Assert.Equal("core|preferences|2", await Slot(quiet));
        Assert.Equal("capsule_changed|mcp|DEMO client", await MemoryLogins.Scalar(s,
            "SELECT event||'|'||channel||'|'||client FROM memory_events WHERE memory_id=@id AND revision=2", ("id", quiet)));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_versions WHERE memory_id=@id AND revision=1", ("id", quiet)));
        // Setting the slot it has answers unchanged, and a stale revision is a conflict.
        Assert.True((bool)(await alex.Ok("set_capsule", new { id = quiet, capsule = "core", slot = "preferences" }))["unchanged"]!);
        Assert.Equal("conflict", (string)(await alex.Refused("set_capsule", new { id = quiet, capsule = "core", slot = "identity", expected_revision = 1 }))["code"]!);

        // Another memory is not put into the slot this one fills, draft though it is: the refusal names it, and nothing
        // is written. A slot name the capsule lacks, a slot with no capsule, and a project slot for a memory in no
        // project are refused too.
        var loud = Id(await alex.Ok("remember", new { content = "DEMO: Alex likes the radio on at breakfast." }));
        var taken = await alex.Refused("set_capsule", new { id = loud, capsule = "core", slot = "preferences" });
        Assert.Equal("invalid", (string)taken["code"]!);
        Assert.Contains(quiet.ToString(), (string)taken["error"]!);
        Assert.Equal("invalid", (string)(await alex.Refused("set_capsule", new { id = loud, capsule = "core", slot = "current-state" }))["code"]!);
        Assert.Equal("invalid", (string)(await alex.Refused("set_capsule", new { id = loud, slot = "identity" }))["code"]!);
        Assert.Equal("invalid", (string)(await alex.Refused("set_capsule", new { id = loud, capsule = "project", slot = "decisions" }))["code"]!);
        Assert.Equal("-|-|1", await Slot(loud));
        // A project's slot is its own: the first memory, in a project, takes one beside its core slot's place.
        Assert.Equal("unknown", (string)(await alex.Refused("set_capsule", new { id = Guid.NewGuid(), capsule = "core", slot = "identity" }))["code"]!);

        // The same writer through update's capsule tags, or through an AI client's channel without the tool, is refused
        // as before: only the tool opens the channel.
        Assert.Equal("forbidden", (string)(await alex.Refused("update", new { id = loud, tags = new[] { "capsule:core", "capsule-slot:identity" } }))["code"]!);
        Assert.Equal("forbidden", await Refused(() => store.SetCapsule(who.Alex, loud, "core", "identity", null, Mcp, default)));
        Assert.Equal("-|-|1", await Slot(loud));

        // Confirmed, the answer says the capsule serves it; taken out, the other memory may take the slot.
        await alex.Ok("set_status", new { id = loud, status = "canonical" });
        var cleared = await alex.Ok("set_capsule", new { id = quiet });
        Assert.Contains("Taken out of its capsule slot", (string)cleared["message"]!);
        Assert.Equal("-|-|3", await Slot(quiet));
        Assert.True((bool)(await alex.Ok("set_capsule", new { id = quiet, capsule = "none" }))["unchanged"]!);
        var served = await alex.Ok("set_capsule", new { id = loud, capsule = "core", slot = "preferences" });
        Assert.Contains("The capsule serves it from its next read on", (string)served["message"]!);
        var capsule = (await new PromptCapsules(s).Build(who.Alex, new("core"), default)).Body;
        Assert.Contains("radio on at breakfast", capsule);
        Assert.DoesNotContain("quiet mornings", capsule);

        // The client's capsule changes are among its changes, which the brief counts, so a person sees them.
        var changes = await store.Changes(who.Alex, null, MemoryTools.Channel, default);
        Assert.Contains(changes.Groups, g => g.Family == "capsule");
        Assert.Contains((await alex.Ok("brief", new { }))["changes"]!["groups"]!.AsArray(), g => (string)g!["family"]! == "capsule");
        // Its own undo of a slot is refused, alone or in a group; the person's undo takes the memory out again.
        Assert.Equal("forbidden", (string)(await alex.Refused("undo", new { id = loud }))["code"]!);
        Assert.Equal("core|preferences|3", await Slot(loud));
        Assert.Equal(["capsule"], (await store.Undo(at.Alex, loud, null, null, Cli, default)).Changed);
        Assert.Equal("-|-|4", await Slot(loud));
    }

    // With memory.capsuleWrites off the tool does not exist and a call naming it changes nothing.
    [PostgresFact] public async Task Without_the_setting_the_tool_is_not_there()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "capsule-tool-off");
        var before = MemoryToolSettings.With("tools: on, capsules: on");
        await using var alex = await As(who.Alex, before);
        var note = Id(await alex.Ok("remember", new { content = "DEMO: The fictional shed key hangs by the door." }));
        Assert.DoesNotContain((await alex.Client.ListToolsAsync()).Select(t => t.Name), n => n == MemoryTools.CapsuleWriteTool);
        await Assert.ThrowsAnyAsync<Exception>(async () => await alex.Client.CallToolAsync(MemoryTools.CapsuleWriteTool,
            new Dictionary<string, object?> { ["id"] = note, ["capsule"] = "core", ["slot"] = "identity" }));
        Assert.Equal("-|-|1", await Slot(note));
    }
}
