using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Knowledge.Cli;

// One adapter's descriptor, contracts/adapters/<name>.json (schema: contracts/adapters/adapter.schema.json): what
// `rhythm connect <name>` asks and writes, and what `rhythm doctor` checks. The engine image carries them in
// /app/adapters (RHYTHM_ADAPTER_DESCRIPTORS overrides the folder).
public sealed partial class AdapterDescriptor
{
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Kinds { get; init; } = [];
    public string Summary { get; init; } = "";
    public bool Unofficial { get; init; }
    public bool DefaultEnabled { get; init; }
    public string? Profile { get; init; }
    public string Docs { get; init; } = "";
    public List<Setting> Settings { get; init; } = [];
    public List<Secret> Secrets { get; init; } = [];
    public bool Connect { get; init; }
    // Its container calls /mcp with a harness token of its own, secrets/harness_<name>_token (`rhythm token create <name>`).
    public bool HarnessToken { get; init; }

    public sealed class Setting
    {
        public string Key { get; init; } = "";
        public string Prompt { get; init; } = "";
        public string Type { get; init; } = "";
        public JsonElement? Default { get; init; }
        public bool Required { get; init; }
        public string? Help { get; init; }
        // A regular expression an answer must match; doctor does not take a matching value for a leaked secret.
        public string? Pattern { get; init; }
    }
    public sealed class Secret
    {
        public string Purpose { get; init; } = "";
        public string Prompt { get; init; } = "";
        public string Source { get; init; } = "";
        public bool Required { get; init; } = true;
        public string? Help { get; init; }
    }

    static readonly string[] KindNames = ["MeasurementSource", "DocumentSource", "MemorySource", "Writer", "TitleModel", "Exposure"];
    static readonly string[] Types = ["string", "int", "bool", "list", "person", "path", "url"];
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static string Folder => Environment.GetEnvironmentVariable("RHYTHM_ADAPTER_DESCRIPTORS") is { Length: > 0 } dir
        ? dir : Path.Combine(AppContext.BaseDirectory, "adapters");

    // Every descriptor in the folder, by name. A broken one stops the command, naming the file and the field.
    public static IReadOnlyList<AdapterDescriptor> Load(string? folder = null)
    {
        folder ??= Folder;
        if (!Directory.Exists(folder)) return [];
        var all = new List<AdapterDescriptor>();
        foreach (var file in Directory.GetFiles(folder, "*.json").Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            AdapterDescriptor d;
            try { d = JsonSerializer.Deserialize<AdapterDescriptor>(File.ReadAllText(file), Options) ?? throw new JsonException("empty"); }
            catch (JsonException e) { throw new ConfigException($"adapter descriptor {Path.GetFileName(file)} is not valid ({e.Message.Split('.')[0]})"); }
            if (d.Problem() is { } problem) throw new ConfigException($"adapter descriptor {Path.GetFileName(file)}: {problem}");
            if (Path.GetFileNameWithoutExtension(file) != d.Name) throw new ConfigException($"adapter descriptor {Path.GetFileName(file)} must be named {d.Name}.json");
            all.Add(d);
        }
        return all;
    }

    // The schema's rules, checked here so a descriptor never makes the CLI write a secret into settings.
    public string? Problem()
    {
        if (!RhythmConfig.AdapterPattern().IsMatch(Name)) return "name is lower case letters, digits or '-', 2-31 characters";
        if (Title.Length is < 1 or > 60) return "title is 1-60 characters";
        if (Kinds.Count == 0 || Kinds.Any(k => !KindNames.Contains(k)) || Kinds.Distinct().Count() != Kinds.Count) return "kinds lists known kinds";
        if (Summary.Length is < 1 or > 200) return "summary is 1-200 characters";
        if (!Docs.StartsWith("docs/", StringComparison.Ordinal)) return "docs is a path under docs/";
        if (Profile is not null && !RhythmConfig.AdapterPattern().IsMatch(Profile)) return "profile is a compose profile name";
        foreach (var s in Settings)
        {
            if (!SettingKey().IsMatch(s.Key) || SecretKey().IsMatch(s.Key) || s.Key == "enabled") return $"settings key {s.Key} is not allowed (never a secret, never enabled)";
            if (!Types.Contains(s.Type)) return $"settings.{s.Key}.type is one of {string.Join(", ", Types)}";
            if (s.Prompt.Length == 0) return $"settings.{s.Key}.prompt is required";
            if (s.Pattern is not null && !ValidPattern(s.Pattern)) return $"settings.{s.Key}.pattern is a regular expression (for a list, of each item)";
        }
        if (Settings.Select(s => s.Key).Distinct().Count() != Settings.Count) return "settings keys must be distinct";
        foreach (var s in Secrets)
        {
            if (!Purpose().IsMatch(s.Purpose)) return $"secrets purpose {s.Purpose} is lower case letters, digits or '_'";
            if (s.Source is not ("paste" or "generate" or "connect")) return $"secrets.{s.Purpose}.source is paste, generate or connect";
            if (s.Prompt.Length == 0) return $"secrets.{s.Purpose}.prompt is required";
        }
        if (Secrets.Select(s => s.Purpose).Distinct().Count() != Secrets.Count) return "secrets purposes must be distinct";
        if (Secrets.Any(s => s.Source == "connect") && !Connect) return "a secret from source connect needs connect: true";
        return null;
    }

    static bool ValidPattern(string pattern)
    {
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,40}$")] private static partial Regex SettingKey();
    [GeneratedRegex("(?i)(token|password|secret|apikey)$")] private static partial Regex SecretKey();
    [GeneratedRegex("^[a-z][a-z0-9_]{1,40}$")] private static partial Regex Purpose();
}
