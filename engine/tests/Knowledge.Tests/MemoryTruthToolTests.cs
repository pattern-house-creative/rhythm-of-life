using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P4-06 and SB-P4-08 through the memory tools (Mcp/MemoryTools.cs, behind memory.tools): supersede's plan and its
// confirmation, update's valid_from and valid_until alone, each memory's state and retracted_sources in get and
// list_recent, and the cascade named in set_status's answer; every answer in its contract's shape (MemoryToolClient),
// every change recorded as an AI client's. Fictional people only; every text is DEMO.
public sealed class MemoryTruthToolTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<MemoryToolClient> As(Guid login)
    {
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(login)).AddSingleton(fixture.Db)
            .AddSingleton(MemoryToolSettings.On).AddSingleton(new Retrieval(fixture.Db)).BuildServiceProvider();
        return await MemoryToolClient.Start(services, MemoryToolSettings.On);
    }

    static Guid Id(JsonNode answer) => Guid.Parse((string)answer["id"]!);

    [PostgresFact] public async Task An_AI_client_replaces_a_fact_only_through_a_confirmed_plan_and_its_retraction_unwinds()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "truth-tools");
        await using var ai = await As(who.Alex);
        var old = Id(await ai.Ok("remember", new { content = "DEMO: the Brindle book club meets on the first Monday." }));
        await ai.Ok("set_status", new { id = old, status = "canonical" });
        var fresh = Id(await ai.Ok("remember", new { content = "DEMO: the Brindle book club meets on the first Thursday now." }));
        var built = Id(await ai.Ok("remember", new { content = "DEMO: so the Brindle club's Thursday room is booked." }));
        await MemoryRows.Run(s, "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(gen_random_uuid(),@b,@f,'drawn_from',@p)",
            ("b", built), ("f", fresh), ("p", who.Alex));

        // A confirmed fact is protected from a draft; confirming the new one is the person's say.
        var refused = await ai.Refused("supersede", new { id = fresh, replaces = old });
        Assert.Equal("protected", (string)refused["code"]!);
        await ai.Ok("set_status", new { id = fresh, status = "canonical" });

        // The plan changes nothing and says how to apply it.
        var plan = await ai.Ok("supersede", new { id = fresh, replaces = old });
        Assert.Equal(("plan", (string?)null), ((string)plan["action"]!, (string?)plan["reason"]));
        var code = (string)plan["confirmation"]!;
        Assert.Contains($"confirmation {code}", (string)plan["message"]!);
        Assert.Equal("current", (string)(await ai.Ok("get", new { id = old }))["memory"]!["state"]!);
        Assert.Equal("conflict", (string)(await ai.Refused("supersede", new { id = fresh, replaces = old, confirmation = "0123456789abcdef" }))["code"]!);

        // Confirmed: the old fact is history, replaced by the new one, and get and list_recent say so.
        var applied = await ai.Ok("supersede", new { id = fresh, replaces = old, confirmation = code });
        Assert.Equal(("superseded", (string?)null), ((string)applied["action"]!, (string?)applied["confirmation"]));
        Assert.Contains($"undo({old})", (string)applied["message"]!);
        var got = (await ai.Ok("get", new { id = old }))["memory"]!;
        Assert.Equal(("replaced", fresh.ToString(), "canonical"), ((string)got["state"]!, (string)got["replaced_by"]!, (string)got["status"]!));
        Assert.NotNull((string?)got["valid_until"]);
        var listed = (await ai.Ok("list_recent", new { n = 10 }))["memories"]!.AsArray();
        Assert.Equal("replaced", (string)listed.Single(m => Id(m!) == old)!["state"]!);
        Assert.Equal("current", (string)listed.Single(m => Id(m!) == fresh)!["state"]!);
        // A retry of the confirmed call changes nothing.
        Assert.Equal(("unchanged", "replaced"), ((string)(await ai.Ok("supersede", new { id = fresh, replaces = old, confirmation = code }))["action"]!,
            (string)(await ai.Ok("supersede", new { id = fresh, replaces = old }))["reason"]!));

        // When it was true goes alone: never beside another field, and never a time to come.
        Assert.Equal("invalid", (string)(await ai.Refused("update", new { id = built, content = "DEMO: other words.", valid_from = "2026-10-01T00:00:00Z" }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("update", new { id = built, valid_until = DateTimeOffset.UtcNow.AddDays(2) }))["code"]!);
        var dated = await ai.Ok("update", new { id = built, valid_from = DateTimeOffset.UtcNow.AddDays(-3) });
        Assert.Equal(["validity"], dated["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Contains("Recorded when it was true", (string)dated["message"]!);
        Assert.Equal("validity", (string)(await ai.Ok("update", new { id = built, clear = new[] { "valid_from" } }))["changed"]![0]!);

        // Marked wrong: the old fact is current again and what was built on the new one is flagged; the answer says so.
        var wrong = await ai.Ok("set_status", new { id = fresh, status = "deprecated" });
        Assert.Contains($"Memory {old} is current again", (string)wrong["message"]!);
        Assert.Contains($"Memory {built}, built on it, is flagged for a check", (string)wrong["message"]!);
        Assert.Equal("current", (string)(await ai.Ok("get", new { id = old }))["memory"]!["state"]!);
        var flagged = (await ai.Ok("get", new { id = built }))["memory"]!;
        Assert.Equal([fresh.ToString()], flagged["retracted_sources"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("retracted", (string)(await ai.Ok("get", new { id = fresh }))["memory"]!["state"]!);
        var history = (await ai.Ok("history", new { id = old }))["changes"]!.AsArray();
        Assert.Equal("reopened", (string)history[0]!["event"]!);
        // Checked and still standing: its flag comes off, alone.
        Assert.Equal("invalid", (string)(await ai.Refused("update", new { id = built, tags = new[] { "club" }, clear = new[] { "retracted_sources" } }))["code"]!);
        // A misspelt name beside it is refused, as anywhere else in clear, and nothing comes off.
        var misspelt = await ai.Refused("update", new { id = built, clear = new[] { "valid_untl", "retracted_sources" } });
        Assert.Equal("invalid", (string)misspelt["code"]!);
        Assert.Single((await ai.Ok("get", new { id = built }))["memory"]!["retracted_sources"]!.AsArray());
        // An idempotency key, as update's text offers it, is taken: clearing is idempotent already.
        var kept = await ai.Ok("update", new { id = built, clear = new[] { "retracted_sources" }, idempotency_key = "truth-tools-clear-1" });
        Assert.Equal(["retracted_sources"], kept["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Empty((await ai.Ok("get", new { id = built }))["memory"]!["retracted_sources"]!.AsArray());
        Assert.True((bool)(await ai.Ok("update", new { id = built, clear = new[] { "retracted_sources" }, idempotency_key = "truth-tools-clear-1" }))["unchanged"]!);

        // Undoing the mistake closes the old fact again, and the answer says so.
        var back = await ai.Ok("undo", new { id = fresh });
        Assert.Contains($"Memory {old} is replaced by this one again", (string)back["message"]!);
        Assert.Equal("replaced", (string)(await ai.Ok("get", new { id = old }))["memory"]!["state"]!);

        // Every change was an AI client's, through MCP.
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=ANY(@ids) AND channel<>'mcp'",
            ("ids", new[] { old, fresh, built })));
    }
}
