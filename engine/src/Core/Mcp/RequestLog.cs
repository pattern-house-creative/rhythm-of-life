namespace Knowledge.Mcp;

// One warning line for each request the engine refuses or cannot answer, so an operator can tell a Host refusal from
// a wrong token from a database fault. Fixed fields only, as the scheduler logs a failing job: the status, a fixed
// code, the role, a known tool or method name and an exception's type name; never a body, value, token, header or
// message. The same line is written at most once a minute, then says how many more there were.
public sealed class RequestLog(ILogger<RequestLog> log, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    const int MaxKinds = 200;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, (DateTimeOffset Quiet, int Skipped)> recent = new(StringComparer.Ordinal);

    // The engine's one log, or none where an endpoint runs without the engine's services (some tests).
    public static RequestLog? Of(HttpContext context) => context.RequestServices?.GetService<RequestLog>();

    // Refused before any endpoint: origin_present, host_not_allowed, not_found, not_ready, no_token, unknown_token,
    // method_not_allowed or wrong_role (with the caller's role); or by the ingest endpoint, plugin_off.
    public void Refused(int status, string reason, string? role = null)
    {
        if (Due($"refused {status} {reason} {role}") is { } skipped)
            log.LogWarning("Request refused: {Status} {Reason}{Role}{Repeats}", status, reason, role is null ? "" : $" (role {role})", Repeats(skipped));
    }

    // A call an endpoint read and did not answer as asked: the caller's role, the tool (or method), the status and
    // code it answered, and the exception's type when one caused it.
    public void Failed(string role, string? tool, int status, string code, Exception? error = null)
    {
        var kind = error?.GetType().Name;
        if (Due($"failed {role} {tool} {status} {code} {kind}") is { } skipped)
            log.LogWarning("Call failed: {Role} {Tool}, {Status} {Code}{Kind}{Repeats}", role, tool ?? "-", status, code,
                kind is null ? "" : $" ({kind})", Repeats(skipped));
    }

    static string Repeats(int skipped) => skipped == 0 ? "" : $"; {skipped} more since the last such line";

    // The number of lines held back since this one was last written, or null while it is held back.
    int? Due(string key)
    {
        var at = now();
        lock (recent)
        {
            if (recent.TryGetValue(key, out var last) && at < last.Quiet) { recent[key] = last with { Skipped = last.Skipped + 1 }; return null; }
            if (recent.Count >= MaxKinds)
                foreach (var old in recent.Where(r => r.Value.Quiet <= at).Select(r => r.Key).ToArray()) recent.Remove(old);
            recent[key] = (at + Window, 0);
            return last.Skipped;
        }
    }
}
