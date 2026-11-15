using System.Text;
using System.Text.Json;

namespace Knowledge.Mcp;

// POST /capture (SB-P7-01; contracts/README.md section 6): a webhook, a CI job or a shortcut saves one memory with a capture
// token of its own. The door (Perimeter) has checked the Origin, the Host, the token and its role, and it opens this route
// only with memory.capture on beside memory.tools; off, the path is no route at all. Everything else is the checked writer's
// (MemoryStore.Capture, migration 067): the token's one scope, a draft or a hold, the channel on the audit row. Plain JSON,
// not JSON-RPC, since a webhook sends what it sends; a refusal has the door's body, so a client reads one shape for all of
// them. Every refusal is a line in the log (RequestLog): the role, the route, the status and code, never the payload. With
// engine.writeLimit on, a token past its limit is refused before its body is read (WriteLimit).
//
// The body is one JSON object. With text and only the fields below it is a capture by those fields; any other object is
// a webhook's own payload, kept whole as the memory's text (Payload; source webhook), since what a vendor sends cannot be
// shaped and losing it helps no one. An object of those fields alone with no text says nothing to save, and is refused.
// The body is at most 288 KiB, the same as the app's capture: a whole memory's text with JSON's short escapes fits.
public static class CaptureEndpoint
{
    public const string Route = "/capture";
    public const int MaxBody = 288 * 1024;
    const string Client = "capture", WholeSource = "webhook";
    static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "text", "tags", "scope", "project", "source", "mirror", "idempotencyKey" };

    public static async Task Handle(HttpContext context, Caller caller, MemoryStore store)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var log = RequestLog.Of(context);
        if (await WriteLimit.Refused(context, caller, Client)) return;
        try
        {
            if (await JsonRpc.ReadBody(context, MaxBody, ct, "A capture is one JSON object.") is not { } body)
            { log?.Failed(caller.Role, Client, context.Response.StatusCode, JsonRpc.Code(context.Response.StatusCode)); return; }
            try
            {
                var (draft, key) = Read(body, context.Request.Headers["Idempotency-Key"].ToString());
                var saved = await store.Capture(caller.Id, draft, caller.Name, key, ct);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(Reply(saved), ct);
            }
            catch (MemoryRefused refused)
            {
                var (status, code) = refused.Code switch
                {
                    "too_large" => (413, "too_large"), "forbidden" => (403, "forbidden"), "key_reused" => (409, "key_reused"), _ => (400, "invalid_arguments"),
                };
                log?.Failed(caller.Role, Client, status, code);
                await JsonRpc.Refuse(context, status, null, code, refused.Message);
            }
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException || e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            log?.Failed(caller.Role, Client, 503, "unavailable", e);
            await JsonRpc.Refuse(context, 503, null, "unavailable", "The database is not answering; retry later.");
        }
    }

    // A body as the store takes it, with its idempotency key (the body's, or the Idempotency-Key header's, which is the one
    // a webhook whose body cannot be shaped can send; the two must agree). Refused (invalid) when it is not one to save.
    public static (CaptureDraft Draft, string? Key) Read(JsonElement body, string? header)
    {
        bool whole;
        // A field's name holding half a surrogate pair cannot be read at all, as no text of ours can hold it.
        try
        {
            if (!JsonRpc.Unique(body)) throw Invalid("A capture names each field once.");
            whole = body.EnumerateObject().Any(m => !Fields.Contains(m.Name));
        }
        catch (InvalidOperationException) { throw Invalid("This payload holds text that cannot be stored."); }
        var given = string.IsNullOrEmpty(header) ? null : header;
        if (whole) return (new(Payload(body), Source: WholeSource), given);
        if (!body.TryGetProperty("text", out var words) || words.ValueKind != JsonValueKind.String) throw Invalid("A capture needs its text in text.");
        var key = Word(body, "idempotencyKey");
        if (key is not null && given is not null && key != given) throw Invalid("The idempotency key in the body and in the Idempotency-Key header differ.");
        var source = Word(body, "source");
        if (MemoryTools.SourceProblem(source) is { } problem) throw Invalid(problem);
        return (new(Text(words), Tags(body), Word(body, "scope"), Word(body, "project"), source, Word(body, "mirror")), key ?? given);
    }

    static MemoryRefused Invalid(string message) => new("invalid", message);

    // A webhook's payload as a memory's text: indented JSON whose strings hold their characters as sent, not escaped, since
    // what the database's screening reads (hidden characters, instruction phrases, 036) must be what a reader will. So a line
    // break or tab inside a value stays one, and only quotes, backslashes and other control characters are written as JSON
    // writes them. It reads as JSON, and parses as JSON unless a value holds such a break.
    public static string Payload(JsonElement body)
    {
        var o = new StringBuilder();
        try { Write(o, body, 0); }
        catch (InvalidOperationException) { throw Invalid("This payload holds text that cannot be stored."); }
        return o.ToString();
    }

    static void Write(StringBuilder o, JsonElement value, int depth)
    {
        void Items<T>(char open, char close, IEnumerable<T> items, Action<T> one)
        {
            o.Append(open);
            var first = true;
            foreach (var item in items)
            {
                o.Append(first ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                one(item);
                first = false;
            }
            if (!first) o.Append('\n').Append(' ', 2 * depth);
            o.Append(close);
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Items('{', '}', value.EnumerateObject(), m => { Quoted(o, m.Name); o.Append(": "); Write(o, m.Value, depth + 1); });
                break;
            case JsonValueKind.Array:
                Items('[', ']', value.EnumerateArray(), v => Write(o, v, depth + 1));
                break;
            case JsonValueKind.String: Quoted(o, value.GetString()!); break;
            default: o.Append(value.GetRawText()); break;
        }
    }

    static void Quoted(StringBuilder o, string text)
    {
        o.Append('"');
        foreach (var c in text)
            switch (c)
            {
                case '"': o.Append("\\\""); break;
                case '\\': o.Append("\\\\"); break;
                case '\n' or '\r' or '\t': o.Append(c); break;
                case < ' ': o.Append("\\u").Append(((int)c).ToString("x4")); break;
                default: o.Append(c); break;
            }
        o.Append('"');
    }

    // A string's value. One holding half a surrogate pair or a NUL is refused here, as no text of ours can hold either: the
    // database's own error for it would answer 503, which tells a webhook to retry for ever.
    static string Text(JsonElement value)
    {
        string text;
        try { text = value.GetString()!; }
        catch (InvalidOperationException) { throw Invalid("This text cannot be stored."); }
        return text.Contains('\0') ? throw Invalid("This text holds a NUL character.") : text;
    }

    static string? Word(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{name} is text.");
        return Text(value);
    }

    static IReadOnlyList<string>? Tags(JsonElement body)
    {
        if (!body.TryGetProperty("tags", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String)) throw Invalid("tags is a list of words.");
        return [.. value.EnumerateArray().Select(Text)];
    }

    // The answer: what became of this write and nothing else, in words a log can show. The first sentence says where it
    // stands, the second who reads it with memory scopes off (D-048).
    public static object Reply(Captured c) => new
    {
        id = c.Id, revision = c.Revision, status = c.Status, scope = c.Scope, scopes = c.Scopes, held = c.Held, mirror = c.Mirror,
        unchanged = c.Unchanged, replayed = c.Replayed, droppedTags = c.DroppedTags,
        message = (c.Held ? "Held for a person's review: search, the map and capsules leave it out until a person releases it." : "Saved as a draft for a person to review.")
            + (c.Scopes == "off" ? " With memory scopes off, everyone in the household can read it." : ""),
    };
}
