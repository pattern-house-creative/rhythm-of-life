using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Knowledge;

// The install's settings, read once from rhythm.yaml (RHYTHM_CONFIG). Nothing here is secret: secrets are files
// passed through *_FILE variables. contracts/rhythm.schema.json is the full schema; the engine checks what it relies
// on and fails at start, with the key's name, when something it needs is wrong.
public sealed partial class RhythmConfig
{
    public const string Household = "household";
    // The synthetic subject the demo data and tests use. Never a person.
    public const string Synthetic = "fixture";

    public int Version { get; init; } = 1;
    public InstanceSection Instance { get; init; } = new();
    public string Timezone { get; init; } = "UTC";
    public List<Person> People { get; init; } = [];
    public DemoSection Demo { get; init; } = new();
    public EngineSection Engine { get; init; } = new();
    public AppSection App { get; init; } = new();
    public GraphSection Graph { get; init; } = new();
    public HealthSection Health { get; init; } = new();
    public Dictionary<string, Dictionary<string, object?>> Adapters { get; init; } = [];
    public Dictionary<string, PluginSection> Plugins { get; init; } = [];
    // Providers that no adapter or plugin owns, whose records the install keeps: those an older engine or a removed
    // plugin wrote (docs/dev/migration-compat.md). Without an entry such a provider is live, and its copies are not served.
    public Dictionary<string, ProviderSection> Providers { get; init; } = [];
    // The memory section as written (memory.scopes, docs/dev/memory.md section 6); MemorySettings.From reads and checks it.
    public object? Memory { get; init; }

    public sealed class InstanceSection { public string Name { get; init; } = "Rhythm of Life"; }
    public sealed class Person { public string Id { get; init; } = ""; public string Name { get; init; } = ""; }
    public sealed class DemoSection { public bool Enabled { get; init; } = true; }
    public sealed class EngineSection
    {
        public string Url { get; init; } = "http://engine:8080";
        public List<string> AllowedHosts { get; init; } = ["localhost", "127.0.0.1", "[::1]", "engine"];
        public string? PublicMcpUrl { get; init; }
        public OAuthSettings? Oauth { get; init; }
        public bool LegacyToolNames { get; init; }
        // search_knowledge and search_resources as before ranking: every word required, newest first (LexicalSearch).
        // Absent means true, so an install whose settings predate ranking keeps the search it had; `rhythm init` and the
        // example settings write false, so a new install ranks (D-053).
        public bool LegacySearch { get; init; } = true;
        // Combined retrieval: off (absent) or on, the recall tool and the explorer's combined search (RecallSettings).
        public string? Recall { get; init; }
        // Which list search_knowledge answers from: fulltext (absent) or combined, with legacySearch false (RecallSettings).
        public string? SearchKnowledge { get; init; }
        // The opt-in recall log: off (absent) or on (RecallSettings, RecallLog).
        public string? RecallLog { get; init; }
        // A cross-encoder reordering combined retrieval's first results: off (absent), bge-reranker-base or
        // ms-marco-minilm (RecallSettings, Reranking).
        public string? Rerank { get; init; }
        // The night summary of the daily jobs: off (absent) or on (NightlySettings, NightlyRunner).
        public string? NightSummary { get; init; }
        // The per-token write limit: off (absent) or on (WriteLimitSettings, WriteLimit).
        public string? WriteLimit { get; init; }
    }
    public sealed class AppSection
    {
        public string? CredentialExpiry { get; init; }
        // The app's address that `rhythm up` prints for people; nothing else reads it.
        public string? PublicUrl { get; init; }
        // Viewer sessions in the app, off or on (ViewerSettings reads and checks it); absent or empty means off.
        public string? Viewers { get; init; }
    }
    public sealed class GraphSection
    {
        public int RefreshSeconds { get; init; } = 900;
        public int FreshnessSeconds { get; init; } = 2100;
        public int MaxMemories { get; init; } = 3000;
        public int MaxDocuments { get; init; } = 1000;
        public int NodeLimit { get; init; } = 300;
        public SimilaritySection Similarity { get; init; } = new();
        public CatalogSection Catalog { get; init; } = new();
        // A plugin's own links between its records, on the map as its suggestions (SB-P4-13): off (absent) or on
        // (RelationshipCatalog.PluginLinks). Absent from every rhythm.yaml written before it, so on upgrade the map is as before.
        public string? PluginLinks { get; init; }
    }
    // The data stars' groups and the providers' labels, before the built-in ones (RelationshipCatalog.For). A group
    // with the id and provider of a built-in one replaces it.
    public sealed class CatalogSection
    {
        public List<CatalogGroup> Groups { get; init; } = [];
        public Dictionary<string, string> Providers { get; init; } = [];
    }
    public sealed class CatalogGroup
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string Provider { get; init; } = "*";
        public List<string> Metrics { get; init; } = [];
        public List<string>? Topics { get; init; }
        public Relationships.RelationshipDataGroup Group => new(Id, Label, Provider, [.. Metrics ?? []], Topics is null ? null : [.. Topics]);
    }
    public sealed class ProviderSection
    {
        public string Policy { get; init; } = "archive";
        public string Domain { get; init; } = "unclassified";
    }
    // Dashed "similar" links, on by default with the values the private version runs; null turns them off. Without the
    // local embeddings model no star has a vector, so they stay off by themselves.
    public sealed class SimilaritySection
    {
        public double? Threshold { get; init; } = 0.6;
        public int Neighbors { get; init; } = 3;
        // Between two documents; null uses Threshold. Ignored while Threshold is null.
        public double? DocumentThreshold { get; init; } = 0.8;
        // Two stars that are each among the other's `Neighbors` closest link from this score up, below the thresholds;
        // null turns that off. Ignored while Threshold is null.
        public double? MutualThreshold { get; init; } = 0.45;
        // The model whose vectors make the similar and copy links (D-256): absent, the live embeddings model's, as before;
        // `bge-small`, a second vector per star made by that model and read by these links alone, with its own recorded
        // thresholds in place of the three above (MapModels). Ignored while Threshold is null.
        public string? Model { get; init; }
        // At most this many similar links a star (D-258): a similar link stays only while it is among the MaxLinks strongest
        // of both its stars, ranked with the mutual rule's; copy links are not counted. Absent: no cap, as before. 2-50;
        // ignored while Threshold is null.
        public int? MaxLinks { get; init; }
    }
    public sealed class HealthSection
    {
        public GlucoseSection Glucose { get; init; } = new();
        public GmiSection Gmi { get; init; } = new();
    }
    public sealed class GlucoseSection
    {
        public string DisplayUnit { get; init; } = "mg/dL";
        public double Low { get; init; } = 70;
        public double High { get; init; } = 180;
    }
    public sealed class GmiSection
    {
        public int MinDays { get; init; } = 14;
        public double MinCoverage { get; init; } = 0.7;
    }
    public sealed class PluginSection
    {
        public bool Enabled { get; init; }
        public List<string> Providers { get; init; } = [];
        public List<string> Subjects { get; init; } = [];
        // The policy of the plugin's providers; Policies overrides it for single providers (a plugin may write a live
        // feed and an archive, as the CareLink plugin does).
        public string Policy { get; init; } = "archive";
        public Dictionary<string, string> Policies { get; init; } = [];
        public string Domain { get; init; } = "unclassified";
        public string PolicyFor(string provider) => Policies?.GetValueOrDefault(provider) ?? Policy;
    }

    // Every subject an install has: each person, then the shared household.
    public IReadOnlyList<string> Subjects => [.. People.Select(p => p.Id), Household];
    public bool IsSubject(string? subject) => subject is not null && Subjects.Contains(subject);
    // The first person: the default subject where a tool needs one person.
    public string FirstPerson => People[0].Id;
    public string DisplayName(string subject) => subject == Household ? "the household"
        : People.FirstOrDefault(p => p.Id == subject)?.Name ?? subject;
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Timezone);
    public DateOnly Today(DateTimeOffset? now = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, Zone).DateTime);

    // An adapter's section, or an empty one (off) when it is not listed.
    public AdapterSettings Adapter(string name) => new(name, Adapters.GetValueOrDefault(name) ?? []);
    public bool AdapterEnabled(string name) => Adapter(name).Enabled;

    // The settings in force, read here by the dashboard records' checks, the tools' static helpers and a few stores.
    // Set once at start (tests set the one TestSettings) and never replaced: a second, different value is refused, so
    // no reader sees the settings change while the engine runs (Q-139).
    static RhythmConfig? current;
    public static RhythmConfig Current
    {
        get => current ?? throw new InvalidOperationException("Settings are not loaded");
        set
        {
            if (Interlocked.CompareExchange(ref current, value, null) is { } set && !ReferenceEquals(set, value))
                throw new InvalidOperationException("Settings are already loaded");
        }
    }
    public static bool Loaded => current is not null;
    // A hash of the settings text, so the map knows when they changed since its last rebuild.
    [YamlIgnore] public string Fingerprint { get; private set; } = "";

    public static RhythmConfig Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ConfigException($"settings file {path} not found (RHYTHM_CONFIG)");
        if (info.Length > 256 * 1024) throw new ConfigException("settings file is larger than 256 KiB");
        return Parse(File.ReadAllText(path));
    }

    public static RhythmConfig Parse(string yaml)
    {
        RhythmConfig config;
        try
        {
            config = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties().Build().Deserialize<RhythmConfig>(yaml) ?? new RhythmConfig();
        }
        catch (YamlDotNet.Core.YamlException e) { throw new ConfigException($"settings are not valid YAML (line {e.Start.Line})"); }
        config.Validate();
        // The deserializer leaves out a key it has no place for, so a misspelt one would read as never set: refused here,
        // once the rules that name a wrong key more closely (a secret in the file, a memory setting) have had their say.
        if (SettingsKeys.Problem(yaml) is { } unknown) throw new ConfigException(unknown);
        config.Fingerprint = Hashing.Hash(yaml);
        return config;
    }

    public void Validate()
    {
        _ = OAuthSettings.From(this);
        if (Version != 1) throw new ConfigException("version must be 1");
        if (string.IsNullOrWhiteSpace(Instance.Name) || Instance.Name.Length > 60) throw new ConfigException("instance.name is 1-60 characters");
        if (!TimezonePattern().IsMatch(Timezone ?? "")) throw new ConfigException("timezone must be an IANA name such as Europe/London, or UTC");
        try { _ = Zone; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ConfigException($"timezone {Timezone} is not known on this system"); }
        if (People is null || People.Count is < 1 or > 20) throw new ConfigException("people lists 1-20 people");
        foreach (var (person, i) in People.Select((p, i) => (p, i)))
        {
            if (person is null || !PersonPattern().IsMatch(person.Id ?? "") || person.Id is Household or Synthetic)
                throw new ConfigException($"people[{i}].id is lower case letters, digits or '-', up to 32 characters, and not household");
            if (string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 60) throw new ConfigException($"people[{i}].name is 1-60 characters");
        }
        if (People.Select(p => p.Id).Distinct().Count() != People.Count) throw new ConfigException("people ids must be distinct");
        if (Engine.AllowedHosts is null || Engine.AllowedHosts.Count is < 1 or > 20 || Engine.AllowedHosts.Any(h => string.IsNullOrWhiteSpace(h) || h.Length > 253))
            throw new ConfigException("engine.allowedHosts lists 1-20 host names");
        if (Graph.RefreshSeconds is < 300 or > 86400) throw new ConfigException("graph.refreshSeconds is 300-86400");
        if (Graph.FreshnessSeconds < Graph.RefreshSeconds + 60 || Graph.FreshnessSeconds > 86400)
            throw new ConfigException("graph.freshnessSeconds is at least graph.refreshSeconds + 60, at most 86400");
        if (Graph.MaxMemories is < 1 or > 4000 || Graph.MaxDocuments is < 0 or > 4000 || Graph.NodeLimit is < 10 or > 500)
            throw new ConfigException("graph.maxMemories (1-4000), graph.maxDocuments (0-4000) or graph.nodeLimit (10-500) is out of range");
        if (Graph.Similarity.Threshold is < 0.2 or > 0.95 || Graph.Similarity.Neighbors is < 1 or > 10
            || Graph.Similarity.DocumentThreshold is < 0.2 or > 0.99 || Graph.Similarity.MutualThreshold is < 0.2 or > 0.95
            || Graph.Similarity.MaxLinks is < 2 or > 50)
            throw new ConfigException("graph.similarity is out of range");
        _ = MapModels.Named(Graph.Similarity.Model);
        if (Graph.PluginLinks is not (null or "" or "off" or "on")) throw new ConfigException("graph.pluginLinks is off or on");
        if (Graph.Catalog?.Groups is null || Graph.Catalog.Providers is null
            || !Relationships.RelationshipCatalog.Valid(new(["household"], [.. Graph.Catalog.Groups.Select(g => g is null ? null! : g.Group)], Graph.Catalog.Providers)))
            throw new ConfigException("graph.catalog: groups have an id, a label, a provider (or *) and 1-100 metric names, and provider labels are 1-60 characters");
        if (Health.Glucose.DisplayUnit is not ("mg/dL" or "mmol/L") || Health.Glucose.Low >= Health.Glucose.High)
            throw new ConfigException("health.glucose: displayUnit is mg/dL or mmol/L and low is below high");
        foreach (var (name, section) in Adapters ?? [])
        {
            if (!AdapterPattern().IsMatch(name)) throw new ConfigException($"adapters.{name}: names are lower case letters, digits or '-'");
            foreach (var key in (section ?? []).Keys)
                if (SecretKey().IsMatch(key)) throw new ConfigException($"adapters.{name}.{key}: settings never hold a secret; use a secret file");
            if (Adapter(name).Owner is { } owner && !IsSubject(owner)) throw new ConfigException($"adapters.{name}.owner must be a person id or household");
        }
        // adapters.calendar: no link in the settings file, the calendar on or off (D-238).
        if (Adapters?.ContainsKey(Knowledge.Adapters.Calendar.CalendarAdapter.AdapterName) == true
            && Knowledge.Adapters.Calendar.CalendarAdapter.LinkRefusal(Adapter(Knowledge.Adapters.Calendar.CalendarAdapter.AdapterName)) is { } link)
            throw new ConfigException(link);
        foreach (var (name, provider) in Providers ?? [])
        {
            if (!AdapterPattern().IsMatch(name) || provider is null) throw new ConfigException($"providers.{name}: names are lower case letters, digits or '-'");
            if (provider.Policy is not ("live" or "archive" or "reference")) throw new ConfigException($"providers.{name}.policy is live, archive or reference");
            if (provider.Domain is not ("health" or "household" or "unclassified")) throw new ConfigException($"providers.{name}.domain is health, household or unclassified");
        }
        foreach (var (name, plugin) in Plugins ?? [])
        {
            if (!AdapterPattern().IsMatch(name)) throw new ConfigException($"plugins.{name}: names are lower case letters, digits or '-'");
            if (plugin is null || plugin.Providers.Count is < 1 or > 10 || plugin.Providers.Any(p => !AdapterPattern().IsMatch(p)))
                throw new ConfigException($"plugins.{name}.providers lists 1-10 provider ids");
            if (plugin.Subjects.Count is < 1 or > 20 || plugin.Subjects.Any(s => !IsSubject(s)))
                throw new ConfigException($"plugins.{name}.subjects lists people ids or household");
            if (plugin.Policy is not ("live" or "archive" or "reference")) throw new ConfigException($"plugins.{name}.policy is live, archive or reference");
            foreach (var (provider, policy) in plugin.Policies ?? [])
            {
                if (!plugin.Providers.Contains(provider)) throw new ConfigException($"plugins.{name}.policies.{provider} is not one of its providers");
                if (policy is not ("live" or "archive" or "reference")) throw new ConfigException($"plugins.{name}.policies.{provider} is live, archive or reference");
            }
            if (plugin.Domain is not ("health" or "household" or "unclassified")) throw new ConfigException($"plugins.{name}.domain is health, household or unclassified");
        }
        // adapters.embeddings: a known model, a shadow other than it, a backfill that is true or false, and known sources.
        _ = EmbeddingSettings.From(this);
        // memory.scopes: off or on (accepted since phase 1's exit, MemorySettings).
        if (MemorySettings.From(this).Refusal() is { } refused) throw new ConfigException(refused);
        // app.viewers: off or on, and on refused until the owner approves viewer sessions (ViewerSettings, D-046).
        if (ViewerSettings.From(this).Refusal() is { } viewers) throw new ConfigException(viewers);
        // engine.recall and engine.searchKnowledge: known values, and the combined search_knowledge only where ranking is on.
        if (RecallSettings.Refusal(this) is { } recall) throw new ConfigException(recall);
        // engine.nightSummary: off or on.
        if (NightlySettings.Refusal(this) is { } night) throw new ConfigException(night);
        // engine.writeLimit: off or on.
        if (Knowledge.Mcp.WriteLimitSettings.Refusal(this) is { } limit) throw new ConfigException(limit);
    }

    [GeneratedRegex("^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+-]+){1,2})$")] private static partial Regex TimezonePattern();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")] private static partial Regex PersonPattern();
    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$")] internal static partial Regex AdapterPattern();
    [GeneratedRegex("([Tt]oken|[Pp]assword|[Ss]ecret|[Aa]pi[Kk]ey)$")] private static partial Regex SecretKey();
}

// One adapter's section of rhythm.yaml: `enabled` plus its own keys, read with defaults.
public sealed class AdapterSettings(string name, IReadOnlyDictionary<string, object?> values)
{
    public string Name { get; } = name;
    public bool Enabled => Bool("enabled", false);
    public string? Owner => String("owner");
    public string? String(string key) => values.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;
    public bool Bool(string key, bool fallback) => String(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;
    public int Int(string key, int fallback) =>
        int.TryParse(String(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public IReadOnlyList<string> List(string key) => values.TryGetValue(key, out var v) && v is IEnumerable<object> items
        ? items.Select(i => i?.ToString() ?? "").ToArray() : [];
    // Whether the key is given as something other than a list (a list key given one plain value, say), so a setting can
    // refuse it rather than read it as empty.
    public bool NotList(string key) => values.TryGetValue(key, out var v) && v is not null && v is not IEnumerable<object>;
    // A secret file named by the adapter convention: RHYTHM_<ADAPTER>_<PURPOSE>_FILE, or else
    // <name>_<purpose> in the adapter secrets folder (RHYTHM_ADAPTER_SECRETS_DIR, default /run/secrets/adapters),
    // which compose mounts from secrets/adapters/ so `rhythm connect` needs no compose change.
    // The account part of this adapter's star ids (provider:account:id): its provider id, unless
    // RHYTHM_<ADAPTER>_STAR_ACCOUNT keeps the account an older engine used, so an existing install's pins, dashboards
    // and insights still name their stars (docs/dev/migration-compat.md).
    public string StarAccount(string provider) =>
        Environment.GetEnvironmentVariable($"RHYTHM_{Name.ToUpperInvariant().Replace('-', '_')}_STAR_ACCOUNT") is { Length: > 0 and <= 500 } kept ? kept : provider;
    public string? SecretFile(string purpose) =>
        Environment.GetEnvironmentVariable($"RHYTHM_{Name.ToUpperInvariant().Replace('-', '_')}_{purpose.ToUpperInvariant().Replace('-', '_')}_FILE")
        is { Length: > 0 } named ? named
        : Path.Combine(Environment.GetEnvironmentVariable("RHYTHM_ADAPTER_SECRETS_DIR") is { Length: > 0 } dir ? dir : "/run/secrets/adapters",
            $"{Name}_{purpose}");
}

public sealed class ConfigException(string message) : Exception(message);
