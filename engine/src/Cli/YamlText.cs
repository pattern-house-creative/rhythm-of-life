using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Knowledge.Cli;

// Small edits to rhythm.yaml that keep the rest of the file, comments included: set one key under a path of block
// mappings, adding the key (or its parents) when it is missing. The caller parses the result again before writing it,
// and Get reads a value back, so an edit this cannot place safely fails instead of writing a wrong file.
public static partial class YamlText
{
    // `value` is YAML text for one line (a scalar or a flow list, see Scalar and List).
    public static string Set(string text, string path, string value) => Edit(text, path, value, null);

    // A block value: lines indented relative to the key, for example "- id: alex" and "  name: Alex".
    public static string SetBlock(string text, string path, IReadOnlyList<string> block) => Edit(text, path, null, block);

    static string Edit(string text, string path, string? value, IReadOnlyList<string>? block)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var keys = path.Split('.');
        int start = 0, end = lines.Count, parent = -1;
        for (var depth = 0; depth < keys.Length; depth++)
        {
            var child = ChildIndent(lines, start, end, parent);
            var at = Find(lines, start, end, child, keys[depth]);
            if (at < 0)
            {
                // Missing: write the rest of the path as nested blocks after the parent's last line.
                var insert = LastContent(lines, start, end) + 1;
                var added = new List<string>();
                for (var d = depth; d < keys.Length; d++)
                {
                    var pad = new string(' ', child + 2 * (d - depth));
                    added.Add(d < keys.Length - 1 || block is not null ? $"{pad}{keys[d]}:" : $"{pad}{keys[d]}: {value}");
                    if (d == keys.Length - 1 && block is not null) added.AddRange(block.Select(b => pad + "  " + b));
                }
                lines.InsertRange(insert, added);
                return string.Join('\n', lines);
            }
            var blockEnd = BlockEnd(lines, at, child);
            var (inline, comment, column) = Split(lines[at], keys[depth]);
            var head = new string(' ', child) + keys[depth] + ":";
            // A trailing comment keeps its column, so the file's comments stay aligned.
            string WithComment(string text) => comment.Length == 0 ? text : text + new string(' ', Math.Max(1, column - text.Length)) + comment;
            if (depth == keys.Length - 1)
            {
                lines.RemoveRange(at + 1, blockEnd - at - 1);
                lines[at] = WithComment(block is null ? $"{head} {value}" : head);
                if (block is not null) lines.InsertRange(at + 1, block.Select(b => new string(' ', child + 2) + b));
                return string.Join('\n', lines);
            }
            if (inline is not ("" or "{}" or "null" or "~"))
                throw new FormatException($"{string.Join('.', keys[..(depth + 1)])} is not a block mapping; edit it by hand");
            lines[at] = WithComment(head);
            (start, end, parent) = (at + 1, blockEnd, child);
        }
        return string.Join('\n', lines);
    }

    // A value read back with a YAML parser: a scalar's text, a list's items joined by ",", or null when absent.
    public static string? Get(string text, string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        YamlNode? node = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        foreach (var key in path.Split('.'))
            node = node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var next) ? next : null;
        return node switch
        {
            YamlScalarNode s => s.Value,
            YamlSequenceNode list => string.Join(',', list.Children.Select(c => (c as YamlScalarNode)?.Value)),
            _ => null,
        };
    }

    // YAML for one value: plain when unambiguous, else a double-quoted (JSON) string, which YAML reads the same way.
    public static string Scalar(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        string s when Plain().IsMatch(s) && !s.Contains(": ", StringComparison.Ordinal) && !Reserved().IsMatch(s) => s,
        string s => JsonSerializer.Serialize(s, Quoting),
        _ => throw new ArgumentException("unsupported value"),
    };
    public static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => Scalar(i))) + "]";

    static readonly JsonSerializerOptions Quoting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    static bool Content(string line) => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#');
    static int ChildIndent(List<string> lines, int start, int end, int parent)
    {
        for (var i = start; i < end; i++) if (Content(lines[i]) && Indent(lines[i]) > parent) return Indent(lines[i]);
        return parent + (parent < 0 ? 1 : 2);
    }
    static int Find(List<string> lines, int start, int end, int indent, string key)
    {
        for (var i = start; i < end; i++)
            if (Content(lines[i]) && Indent(lines[i]) == indent && KeyOf(lines[i]) == key) return i;
        return -1;
    }
    static string? KeyOf(string line) => KeyLine().Match(line) is { Success: true } m ? m.Groups[1].Value.Trim('"', '\'') : null;
    // The lines that belong to the key at `at`: deeper ones, and list items at its own indent; not trailing comments.
    static int BlockEnd(List<string> lines, int at, int indent)
    {
        var last = at;
        for (var i = at + 1; i < lines.Count; i++)
        {
            if (!Content(lines[i])) continue;
            if (Indent(lines[i]) > indent || Indent(lines[i]) == indent && lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal)) last = i;
            else break;
        }
        return last + 1;
    }
    static int LastContent(List<string> lines, int start, int end)
    {
        for (var i = end - 1; i >= start; i--) if (Content(lines[i])) return i;
        return start - 1;
    }
    // The text after "key:", split into its value and a trailing comment (a # after a space, outside quotes), with
    // the comment's column.
    static (string Inline, string Comment, int Column) Split(string line, string key)
    {
        var start = line.IndexOf(':', Indent(line) + key.Length) + 1;
        var rest = line[start..];
        char quote = '\0';
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || rest[i - 1] is ' ' or '\t')) return (rest[..i].Trim(), rest[i..], start + i);
        }
        return (rest.Trim(), "", 0);
    }

    [GeneratedRegex(@"^\s*(""[^""]+""|'[^']+'|[^\s#:'""][^:#]*?)\s*:(\s|$)")] private static partial Regex KeyLine();
    [GeneratedRegex(@"^[A-Za-z0-9_./][A-Za-z0-9_./:@+ -]*$")] private static partial Regex Plain();
    [GeneratedRegex(@"^(?i:true|false|yes|no|on|off|null|y|n|[-+]?[0-9][0-9_.eE+-]*|\.inf|\.nan)$|[\s:]$")] private static partial Regex Reserved();
}
