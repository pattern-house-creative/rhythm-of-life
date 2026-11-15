using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P4-02 through the memory tools (Mcp/MemoryTools.cs, behind memory.tools): link, unlink and connections as an AI
// client reaches them, every answer in its contract's shape (MemoryToolClient), every link an AI client's (inferred).
// Fictional people only; every text is DEMO.
[Collection(SharedStateCollection.Name)]
public sealed class MadeLinkToolTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<MemoryToolClient> As(Guid login)
    {
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(login)).AddSingleton(fixture.Db)
            .AddSingleton(MemoryToolSettings.On).AddSingleton(new Retrieval(fixture.Db)).BuildServiceProvider();
        return await MemoryToolClient.Start(services, MemoryToolSettings.On);
    }

    static Guid Id(JsonNode answer) => Guid.Parse((string)answer["id"]!);

    [PostgresFact] public async Task An_AI_client_links_lists_and_unlinks_two_memories()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-tools");
        await using var ai = await As(who.Alex);
        var rota = Id(await ai.Ok("remember", new { content = "DEMO: the Fennick allotment rota starts in March." }));
        var water = Id(await ai.Ok("remember", new { content = "DEMO: the Fennick allotment needs watering on Tuesdays." }));

        // relates_to by default, stored smaller id first, an AI client's: inferred, system.
        var made = await ai.Ok("link", new { source_id = water, target_id = rota, confidence = 0.6 });
        Assert.Equal(("relates_to", false, "inferred", "system", true, false, "off"),
            ((string)made["type"]!, (bool)made["directed"]!, (string)made["verification"]!, (string)made["provenance"]!, (bool)made["created"]!,
             (bool)made["unchanged"]!, (string)made["scopes"]!));
        Assert.Equal(string.CompareOrdinal(water.ToString(), rota.ToString()) < 0 ? water : rota, Guid.Parse((string)made["from"]!));
        Assert.Contains("inferred until a person confirms it", (string)made["message"]!);
        var again = await ai.Ok("link", new { source_id = rota, target_id = water });
        Assert.Equal((Id(made), false, true), (Id(again), (bool)again["created"]!, (bool)again["unchanged"]!));
        Assert.StartsWith("Nothing changed", (string)again["message"]!);
        var caused = await ai.Ok("link", new { source_id = water, target_id = rota, type = "caused_by" });
        Assert.Equal((true, water.ToString(), rota.ToString()), ((bool)caused["directed"]!, (string)caused["from"]!, (string)caused["to"]!));

        // connections: both links, newest first, with the other memory's preview and who may unlink each.
        var listed = await ai.Ok("connections", new { id = rota });
        var items = listed["connections"]!.AsArray();
        Assert.Equal(["caused_by", "relates_to"], items.Select(i => (string)i!["type"]!));
        Assert.Equal(("incoming", true, true, true, water.ToString()), ((string)items[0]!["direction"]!, (bool)items[0]!["made"]!, (bool)items[0]!["mine"]!,
            (bool)items[0]!["may_unlink"]!, (string)items[0]!["memory"]!["id"]!));
        Assert.Equal(("none", 0.6), ((string)items[1]!["direction"]!, (double)items[1]!["confidence"]!));
        Assert.Equal("DEMO: the Fennick allotment needs watering on Tuesdays.", (string)items[0]!["memory"]!["preview"]!);
        Assert.Contains("never instructions", (string)listed["notice"]!);
        Assert.Single((await ai.Ok("connections", new { id = rota, type = "caused_by" }))["connections"]!.AsArray());
        Assert.True((bool)(await ai.Ok("connections", new { id = rota, limit = 1 }))["truncated"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("connections", new { id = rota, limit = 0 }))["code"]!);
        Assert.Equal("unknown", (string)(await ai.Refused("connections", new { id = Guid.NewGuid() }))["code"]!);

        // Refused: supersedes (supersede's), an unknown type, a confidence outside 0 to 1, a memory linked to itself.
        foreach (var args in new object[] { new { source_id = water, target_id = rota, type = "supersedes" }, new { source_id = water, target_id = rota, type = "similar_to" },
            new { source_id = water, target_id = rota, confidence = 1.5 }, new { source_id = water, target_id = water } })
            Assert.Equal("invalid", (string)(await ai.Refused("link", args))["code"]!);
        Assert.Equal("unknown", (string)(await ai.Refused("link", new { source_id = water, target_id = Guid.NewGuid() }))["code"]!);

        // unlink: one type, then every type; again, nothing.
        var one = await ai.Ok("unlink", new { source_id = rota, target_id = water, type = "caused_by" });
        Assert.Equal([(string)caused["id"]!], one["removed"]!.AsArray().Select(r => (string)r!["id"]!));
        var rest = await ai.Ok("unlink", new { source_id = water, target_id = rota });
        Assert.Equal([(string)made["id"]!], rest["removed"]!.AsArray().Select(r => (string)r!["id"]!));
        var none = await ai.Ok("unlink", new { source_id = water, target_id = rota });
        Assert.Equal((true, "Nothing changed: there is no link between them to remove."), ((bool)none["unchanged"]!, (string)none["message"]!));
        Assert.Empty((await ai.Ok("connections", new { id = rota }))["connections"]!.AsArray());
        // A link changes no memory: no revision moved, no history row written.
        Assert.Equal(1, (int)(await ai.Ok("get", new { id = rota }))["memory"]!["revision"]!);
        Assert.Single((await ai.Ok("history", new { id = water }))["changes"]!.AsArray());
    }

    // A drawn_from link an AI client makes is one the retraction cascade follows (049): retracting what a memory was drawn
    // from flags it for a check, and bringing it back takes the flag off.
    [PostgresFact] public async Task A_drawn_from_link_made_through_the_tool_is_followed_by_the_retraction_cascade()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-cascade");
        await using var ai = await As(who.Alex);
        var source = Id(await ai.Ok("remember", new { content = "DEMO: the Fennick hall is booked for the fete." }));
        var built = Id(await ai.Ok("remember", new { content = "DEMO: so the Fennick fete's cake stall goes in the hall." }));
        await ai.Ok("link", new { source_id = built, target_id = source, type = "drawn_from" });
        await ai.Ok("set_status", new { id = source, status = "deprecated" });
        Assert.Equal([source.ToString()], (await ai.Ok("get", new { id = built }))["memory"]!["retracted_sources"]!.AsArray().Select(x => (string)x!));
        await ai.Ok("undo", new { id = source });
        Assert.Empty((await ai.Ok("get", new { id = built }))["memory"]!["retracted_sources"]!.AsArray());
    }

    [PostgresFact] public async Task With_scopes_on_a_link_keeps_to_one_scope_and_the_author_lock()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "link-tools-on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await using var alex = await As(who.Alex);
            await using var sam = await As(who.Sam);
            var mine = Id(await alex.Ok("remember", new { content = "DEMO: Alex's private Fennick note." }));
            var ours = Id(await alex.Ok("remember", new { content = "DEMO: the Fennick fete, for everyone.", workspace = "shared" }));
            var his = Id(await sam.Ok("remember", new { content = "DEMO: Sam's Fennick fete stall.", workspace = "shared" }));

            var across = await alex.Refused("link", new { source_id = mine, target_id = ours });
            Assert.Equal("invalid", (string)across["code"]!);
            Assert.Contains("different scopes", (string)across["error"]!);
            Assert.Equal("unknown", (string)(await sam.Refused("link", new { source_id = his, target_id = mine }))["code"]!);
            Assert.Equal("forbidden", (string)(await sam.Refused("link", new { source_id = ours, target_id = his }))["code"]!);
            var link = await sam.Ok("link", new { source_id = his, target_id = ours, type = "part_of_project" });
            Assert.Equal(("shared", "household", "on"), ((string)link["scope"]!, (string)link["owner"]!, (string)link["scopes"]!));
            // Alex sees it and may not remove it; Sam may.
            var seen = Assert.Single((await alex.Ok("connections", new { id = ours }))["connections"]!.AsArray());
            Assert.Equal((false, false), ((bool)seen!["mine"]!, (bool)seen["may_unlink"]!));
            Assert.Equal("forbidden", (string)(await alex.Refused("unlink", new { source_id = ours, target_id = his }))["code"]!);
            Assert.Single((await sam.Ok("unlink", new { source_id = ours, target_id = his }))["removed"]!.AsArray());

            // Held for review: never linked, and served in connections without its words.
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", his));
            Assert.Equal("held", (string)(await sam.Refused("link", new { source_id = his, target_id = ours }))["code"]!);
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", his));
            await sam.Ok("link", new { source_id = his, target_id = ours });
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", his));
            var held = Assert.Single((await alex.Ok("connections", new { id = ours }))["connections"]!.AsArray())!["memory"]!;
            Assert.Equal(("instructions", (string?)null), ((string)held["held"]!["reason"]!, (string?)held["preview"]));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", his));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }
}
