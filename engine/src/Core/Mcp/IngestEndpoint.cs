using System.Text.Json;

namespace Knowledge.Mcp;

// The ingest role (contracts/README.md 4): an external plugin's token sees exactly ingest_measurements and
// ingest_records, stateless, body at most 512 KiB. A plugin whose entry in rhythm.yaml is missing or off gets 403
// for every call. Every refusal, a write the contract refuses included, is a line in the log (RequestLog): the role,
// the tool, the status and code, never the payload. With engine.writeLimit on, a plugin past its token's limit gets 429 for a
// tools/call and nothing is written; its handshake and tools/list answer as ever (WriteLimit).
public static class IngestEndpoint
{
    public const string Measurements = "ingest_measurements", Records = "ingest_records";
    public const int MaxBody = 512 * 1024;

    public static async Task Handle(HttpContext context, Caller caller, Ingest ingest, RhythmConfig config)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var log = RequestLog.Of(context);
        if (caller.Plugin is not { } plugin || !config.Plugins.TryGetValue(plugin, out var entry) || !entry.Enabled)
        {
            log?.Refused(403, "plugin_off", caller.Role);
            await JsonRpc.Refuse(context, 403, null, "forbidden", "This plugin is not enabled in rhythm.yaml."); return;
        }
        string? label = null;
        try
        {
            if (await JsonRpc.ReadBody(context, MaxBody, ct) is not { } request)
            { log?.Failed(caller.Role, null, context.Response.StatusCode, JsonRpc.Code(context.Response.StatusCode)); return; }
            var known = JsonRpc.Envelope(request, out var method, out var tool, out var args);
            label = tool is Measurements or Records ? tool : method is "tools/call" ? "unknown_tool"
                : method is "initialize" or "ping" or "tools/list" or "notifications/initialized" ? method : null;
            if (!known || method == "tools/call" && tool is not (Measurements or Records) || args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            {
                var (status, code) = method == "tools/call" ? (403, "forbidden") : (400, "invalid_arguments");
                log?.Failed(caller.Role, label, status, code);
                await JsonRpc.Refuse(context, status, request, code, "Not an ingest call."); return;
            }
            if (method == "notifications/initialized") { context.Response.StatusCode = 202; return; }
            object result;
            switch (method)
            {
                case "initialize": result = JsonRpc.Initialize("rhythm-engine-ingest"); break;
                case "ping": result = new { }; break;
                case "tools/list": result = new { tools = Tools() }; break;
                default:
                    if (await WriteLimit.Refused(context, caller, tool!, request)) return;
                    try
                    {
                        var written = tool == Measurements ? await ingest.Measurements(plugin, args, ct) : await ingest.Records(plugin, args, ct);
                        result = JsonRpc.Result(written, tool == Measurements
                            ? $"{written["accepted"]} measurements accepted ({written["inserted"]} new, {written["updated"]} updated, {written["unchanged"]} unchanged)."
                            : $"{written["created"]} records created, {written["updated"]} updated, {written["unchanged"]} unchanged.");
                    }
                    catch (Ingest.Refused refused) { log?.Failed(caller.Role, label, 200, refused.Code); result = JsonRpc.Failure(refused.Error()); }
                    break;
            }
            await JsonRpc.Write(context, request, result, ct);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException || e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            log?.Failed(caller.Role, label, 503, "unavailable", e);
            await JsonRpc.Refuse(context, 503, null, "unavailable", "The database is not answering; retry later.");
        }
    }

    public static object[] Tools() =>
    [
        new { name = Measurements,
            description = "Write numeric samples (time, metric, value, unit) under one source record, all or nothing. See the ingest contract: provider and subject must be among this plugin's configured ones; idempotencyKey replays the first result for the same payload.",
            inputSchema = new { type = "object", additionalProperties = false, required = new[] { "provider", "subject", "idempotencyKey", "source", "measurements" },
                properties = new { provider = new { type = "string" }, subject = new { type = "string" }, idempotencyKey = new { type = "string", pattern = "^[A-Za-z0-9._:-]{8,200}$" },
                    source = new { type = "object" }, measurements = new { type = "array", minItems = 1, maxItems = Ingest.MaxMeasurements, items = new { type = "object" } } } },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false } },
        new { name = Records,
            description = "Write text records (settings, events, notes, reports) with optional links between them, all or nothing. Same rules as ingest_measurements.",
            inputSchema = new { type = "object", additionalProperties = false, required = new[] { "provider", "subject", "idempotencyKey", "records" },
                properties = new { provider = new { type = "string" }, subject = new { type = "string" }, idempotencyKey = new { type = "string", pattern = "^[A-Za-z0-9._:-]{8,200}$" },
                    records = new { type = "array", minItems = 1, maxItems = Ingest.MaxRecords, items = new { type = "object" } } } },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false } },
    ];
}
