using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace Knowledge.Mcp;

// engine.writeLimit (docs/configuration.md): off (absent) or on. Absent from every rhythm.yaml written before it, so on
// upgrade no token is counted and every tool answers as it did.
public static class WriteLimitSettings
{
    public static bool On(RhythmConfig config) => config.Engine.WriteLimit is "on";

    // Why the setting is refused at start, naming the key, or null.
    public static string? Refusal(RhythmConfig config) =>
        config.Engine.WriteLimit is null or "" or "off" or "on" ? null : "engine.writeLimit is off or on";
}

// A token's writes in any ten minutes, past which its next write is refused and nothing is saved (SB-P7-12; ledger SEC-18;
// docs/security.md). It stands behind the burst rule, on that rule's window: a token's first 40 memory writes in ten minutes
// save as written, the next are saved but held for a person (MemoryHolds, migrations 045 and 067), and past Writes none is
// saved until the oldest leaves the window. The app's role and the terminal are not here: the app's endpoint, its shared login
// included, and the terminal never ask, and their logins are never counted. Reads never ask either; a token past its limit
// reads as ever.
//
// Three places ask, one for each way a token writes: a harness tool that does not say it is read only (CheckedTool, so a
// tool nobody marked counts), a plugin's tools/call (IngestEndpoint) and POST /capture (CaptureEndpoint). They ask past the
// door, not in it (Perimeter), because only they know what a call does: the door has not read the body.
//
// Only with engine.writeLimit on is there one of these (Serve): off, no service, so nothing asks and nothing is counted.
// The count lives in this one process, as a screen's wait does, and needs no database, so a refused call reaches none and
// a read adds nothing to it. A restart forgives what was counted. Its clock is monotonic, so a wall clock stepping back
// neither holds a token past its window nor lets a window end early.
public sealed class WriteLimit(Func<TimeSpan>? clock = null)
{
    // The numbers and how they were chosen are in docs/security.md: 300 clears the busiest token of the end-to-end runs (33
    // writes, an AI client in cli/journeys.sh) and of the engine's tests (202), and every write the synthetic corpus takes
    // through one token in one sitting (267).
    public const int Writes = 300;
    public static readonly TimeSpan Window = MemoryHolds.BurstWindow;
    public const string Code = "write_limit";

    static readonly long Started = Stopwatch.GetTimestamp();
    readonly Func<TimeSpan> now = clock ?? (() => Stopwatch.GetElapsedTime(Started));
    readonly Dictionary<Guid, Queue<TimeSpan>> taken = [];

    // This install's limit in the services, or none. Registered once, by the one MCP server every door shares.
    public static IServiceCollection Serve(IServiceCollection services, RhythmConfig config)
    {
        if (WriteLimitSettings.On(config)) services.TryAddSingleton(new WriteLimit());
        return services;
    }

    public static WriteLimit? Of(IServiceProvider? services) => services?.GetService<WriteLimit>();

    // One write by this token: counted and null when it has room, else how long until it has. A refusal counts nothing, so a
    // client that keeps asking only waits for what it already wrote to leave the window.
    public TimeSpan? Take(Guid token)
    {
        var at = now();
        lock (taken)
        {
            if (!taken.TryGetValue(token, out var writes)) taken[token] = writes = new();
            while (writes.TryPeek(out var oldest) && at - oldest >= Window) writes.Dequeue();
            if (writes.Count >= Writes) return writes.Peek() + Window - at;
            writes.Enqueue(at);
            return null;
        }
    }

    static int Seconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    // The one refusal in words, the same everywhere: what happened, what did not, and when it can try again.
    public static string Message(TimeSpan wait) =>
        $"This token has made {Writes} writes in the last {Window.TotalMinutes:0} minutes, its limit (engine.writeLimit), so this write changed nothing. "
        + $"It can write again in {Seconds(wait)} second{(Seconds(wait) == 1 ? "" : "s")}; reads still answer.";

    // A harness tool's call: the refusal when it is past its limit, or null to go on. A read never counts: only a tool that
    // says so is one. A memory tool answers as its other refusals do (contracts/README.md 2.1), every other as one line of text.
    public static CallToolResult? Refused(Tool tool, IServiceProvider? services)
    {
        if (tool.Annotations?.ReadOnlyHint == true || services is null || Of(services) is not { } limit
            || services.GetService<IHttpContextAccessor>()?.HttpContext?.Items["principal"] is not Guid token
            || limit.Take(token) is not { } wait) return null;
        services.GetService<RequestLog>()?.Failed("harness", tool.Name, 429, Code);
        return MemoryTools.Answers(tool.Name) ? MemoryTools.Limited(Message(wait))
            : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Message(wait) }] };
    }

    // A call at a door of its own (a plugin's, a capture's): true after answering 429 with Retry-After and the door's body.
    public static async Task<bool> Refused(HttpContext context, Caller caller, string tool, JsonElement? request = null)
    {
        if (Of(context.RequestServices)?.Take(caller.Id) is not { } wait) return false;
        RequestLog.Of(context)?.Failed(caller.Role, tool, 429, Code);
        context.Response.Headers.RetryAfter = Seconds(wait).ToString(CultureInfo.InvariantCulture);
        await JsonRpc.Refuse(context, 429, request, Code, Message(wait));
        return true;
    }
}
