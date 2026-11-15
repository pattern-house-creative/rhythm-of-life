using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowledge.Mcp;

// A harness tool's arguments, checked against the input schema the tool lists before the SDK binds them. Without it a
// missing required argument, an unknown one or a wrong type came back as the SDK's "An error occurred invoking"; now
// the answer names the argument and what it takes, so the AI can correct its call. Values are never echoed: an
// argument's value may be anything a person typed. A memory tool (SB-P3-09; share, SB-P3-10, too) answers the problem
// as its other refusals, {ok: false, code: invalid, error} (contracts/README.md 2.1); every other tool as one line of
// text. A call whose arguments fit then meets its token's write limit, with engine.writeLimit on (WriteLimit), which only a
// tool not marked read only counts.
public sealed class CheckedTool(McpServerTool tool) : DelegatingMcpServerTool(tool)
{
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        ToolArguments.Problem(ProtocolTool, request.Params?.Arguments) is { } problem
            ? ValueTask.FromResult(MemoryTools.Answers(ProtocolTool.Name) ? MemoryTools.Invalid(problem)
                : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = problem }] })
            : WriteLimit.Refused(ProtocolTool, request.Services) is { } limited ? ValueTask.FromResult(limited)
            : base.InvokeAsync(request, cancellationToken);
}

public static partial class ToolArguments
{
    // The first problem with these arguments, or null when they fit the tool's schema. Checks the subset the SDK writes
    // for method parameters: type (with null), required, properties, items, enum and the date-time, date and uuid formats.
    public static string? Problem(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var schema = tool.InputSchema;
        arguments ??= new Dictionary<string, JsonElement>();
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var names = properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Select(x => x.Name).ToArray() : [];
        foreach (var name in arguments.Keys)
            if (!names.Contains(name))
                return $"{(Plain().IsMatch(name) ? name : "That argument")} is not an argument of {tool.Name}; "
                    + (names.Length == 0 ? "it takes none." : $"it takes {string.Join(", ", names)}.");
        return Mismatch(schema, JsonSerializer.SerializeToElement(arguments), "");
    }

    static string? Mismatch(JsonElement schema, JsonElement? value, string at)
    {
        // JSON Schema's true schema is the SDK's shape for an opaque JsonElement (typed controls validate it after binding).
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return $"{at} is not allowed.";
        if (value is not { } v) return null;
        var types = Types(schema);
        if (types.Count > 0 && !types.Any(t => Is(t, v))) return $"{at} must be {Expected(schema)}.";
        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array
            && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, v))) return $"{at} must be {Expected(schema)}.";
        if (v.ValueKind == JsonValueKind.String && schema.TryGetProperty("format", out var format) && !Formatted(format.GetString(), v))
            return $"{at} must be {Expected(schema)}.";
        if (v.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in v.EnumerateArray())
                if (Mismatch(items, item, $"{at}[{i++}]") is { } wrong) return wrong;
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            string Path(string name) => at.Length == 0 ? name : $"{at}.{name}";
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    if (!v.TryGetProperty(name, out _))
                        return $"{Path(name)} is required: {(properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var s) ? Expected(s) : "a value")}.";
            if (properties.ValueKind == JsonValueKind.Object)
                foreach (var member in v.EnumerateObject())
                    if (properties.TryGetProperty(member.Name, out var s) && Mismatch(s, member.Value, Path(member.Name)) is { } wrong) return wrong;
        }
        return null;
    }

    static List<string> Types(JsonElement schema) =>
        !schema.TryGetProperty("type", out var type) ? []
        : type.ValueKind == JsonValueKind.Array ? [.. type.EnumerateArray().Select(t => t.GetString()!)]
        : [type.GetString()!];

    static bool Is(string type, JsonElement v) => type switch
    {
        "null" => v.ValueKind == JsonValueKind.Null,
        "string" => v.ValueKind == JsonValueKind.String,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
        "number" => v.ValueKind == JsonValueKind.Number,
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    // The formats the SDK writes for DateTimeOffset, DateOnly and Guid, read as System.Text.Json will read them.
    static bool Formatted(string? format, JsonElement v) => format switch
    {
        "date-time" => v.TryGetDateTimeOffset(out _),
        "date" => DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "uuid" => v.TryGetGuid(out _),
        _ => true,
    };

    // What a schema takes, in words: "a whole number", "a date such as 2026-10-28 or null", "one of csv, live".
    static string Expected(JsonElement schema)
    {
        var types = Types(schema);
        var format = schema.TryGetProperty("format", out var f) ? f.GetString() : null;
        var what = schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array
            ? "one of " + string.Join(", ", choices.EnumerateArray().Where(c => c.ValueKind != JsonValueKind.Null).Select(c => c.ToString()))
            : types.FirstOrDefault(t => t != "null") switch
            {
                "string" => format switch
                {
                    "date-time" => "a date and time such as 2026-10-28T12:00:00Z",
                    "date" => "a date such as 2026-10-28",
                    "uuid" => "a UUID",
                    _ => "a string",
                },
                "integer" => "a whole number",
                "number" => "a number",
                "boolean" => "true or false",
                "array" => "a list",
                "object" => "an object",
                _ => "a value",
            };
        return types.Contains("null") ? what + " or null" : what;
    }

    // Argument names worth repeating back: short identifiers, never free text.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex Plain();
}
