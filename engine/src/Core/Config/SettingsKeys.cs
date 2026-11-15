using System.Reflection;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Knowledge;

// The keys of rhythm.yaml this version knows, from the settings schema (contracts/rhythm.schema.json, built into the
// engine). A key the schema does not name is refused when the settings load, with its place and the nearest known key:
// read as it was written, a misspelt key was simply left out, so `engine.writelimit: on` kept the write limit off and
// nothing said so. Only key names are checked here; each value's own rule is the engine's (RhythmConfig.Validate).
public static class SettingsKeys
{
    const string Resource = "Knowledge.Contracts.rhythm.schema.json";
    static readonly Lazy<JsonObject> Schema = new(() =>
    {
        using var stream = typeof(SettingsKeys).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not built into the engine");
        return JsonNode.Parse(stream)!.AsObject();
    });

    public sealed record Unknown(string Path, int Line, string? Nearest);

    // Every key in the settings text that its place in the schema does not allow, in the order written. Text that is not
    // YAML, or whose top level is not a mapping, gives none: the settings' own loading says what is wrong with it.
    public static IReadOnlyList<Unknown> Find(string yaml)
    {
        var found = new List<Unknown>();
        YamlNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
        }
        catch (YamlDotNet.Core.YamlException) { return found; }
        // By reference: two mappings that read alike are still two places, each checked against its own schema.
        if (root is not null) Walk(root, Schema.Value, "", found, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        return found;
    }

    // What to tell the operator, or null when every key is known.
    public static string? Problem(string yaml)
    {
        var unknown = Find(yaml);
        if (unknown.Count == 0) return null;
        var each = unknown.Select(u => $"{u.Path} (line {u.Line}{(u.Nearest is null ? "" : $"; did you mean {u.Nearest}?")})");
        return (unknown.Count == 1 ? "rhythm.yaml has a setting this version does not know: " : "rhythm.yaml has settings this version does not know: ")
            + string.Join(", ", each) + ". Keys are case-sensitive; docs/configuration.md lists them.";
    }

    static void Walk(YamlNode node, JsonObject schema, string path, List<Unknown> found, HashSet<YamlNode> seen)
    {
        if (!seen.Add(node)) return; // an alias back to a node already walked
        if (node is YamlSequenceNode list)
        {
            if (Items(schema) is { } items) foreach (var item in list.Children) Walk(item, items, path + "[]", found, seen);
            return;
        }
        if (node is not YamlMappingNode map) return;
        var known = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var open = false;      // any key may be here, each read by `other`
        var closed = false;    // only the keys named may be here
        JsonObject? other = null;
        foreach (var part in Parts(schema))
        {
            if (part["properties"] is JsonObject properties)
                foreach (var (name, child) in properties) known[name] = child as JsonObject ?? known.GetValueOrDefault(name);
            switch (part["additionalProperties"])
            {
                case JsonObject any: open = true; other = any; break;
                case JsonValue value when value.TryGetValue(out bool allowed) && !allowed: closed = true; break;
            }
            if (part["unevaluatedProperties"] is JsonValue rest && rest.TryGetValue(out bool evaluated) && !evaluated) closed = true;
        }
        foreach (var (keyNode, value) in map.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } || key == "<<") continue;
            var at = path.Length == 0 ? key : path + "." + key;
            if (known.TryGetValue(key, out var child)) { if (child is not null) Walk(value, child, at, found, seen); }
            else if (open) { if (other is not null) Walk(value, other, at, found, seen); }
            else if (closed) found.Add(new(at, (int)keyNode.Start.Line, Nearest(key, known.Keys) is { } near ? (path.Length == 0 ? near : path + "." + near) : null));
        }
    }

    // A schema and everything it is made of at this place: what its $ref names, and each branch of allOf, anyOf and oneOf.
    static IEnumerable<JsonObject> Parts(JsonObject schema, int depth = 0)
    {
        yield return schema;
        if (depth > 8) yield break;
        if (schema["$ref"] is JsonValue reference && reference.TryGetValue(out string? pointer) && Resolve(pointer) is { } target)
            foreach (var part in Parts(target, depth + 1)) yield return part;
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (schema[keyword] is JsonArray branches)
                foreach (var branch in branches.OfType<JsonObject>())
                    foreach (var part in Parts(branch, depth + 1)) yield return part;
    }

    static JsonObject? Items(JsonObject schema) => Parts(schema).Select(p => p["items"]).OfType<JsonObject>().FirstOrDefault();

    static JsonObject? Resolve(string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonNode? at = Schema.Value;
        foreach (var step in pointer[2..].Split('/')) at = (at as JsonObject)?[step];
        return at as JsonObject;
    }

    // The known key this one most likely meant: the same letters in another case, or one or two letters away.
    static string? Nearest(string key, IEnumerable<string> known)
    {
        string? best = null;
        var least = 3;
        foreach (var candidate in known)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) return candidate;
            var distance = Distance(key.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < least) { least = distance; best = candidate; }
        }
        return best;
    }

    static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
