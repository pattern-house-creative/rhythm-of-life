using System.Text.RegularExpressions;

namespace Knowledge;

public enum SourceAuthority { LiveSource, AuthorizedArchive, ReferenceOnly }

// Trusted adapter configuration, never supplied by an API caller or source metadata.
// Archive authority means an independently authorized copy, not simply a cached live source.
public sealed record ProviderPolicy(SourceAuthority Authority, string DefaultDomain = "unclassified",
    bool IndividualPrivateOnly = false);

public sealed class ProviderPolicies
{
    readonly IReadOnlyDictionary<string, ProviderPolicy> policies;
    // The providers the core itself writes. Every other provider belongs to an adapter or a plugin, which registers
    // its own policy (contracts/README.md 3.2); a provider nobody registered is live and never a trusted archive.
    public static IReadOnlyDictionary<string, ProviderPolicy> Core { get; } = new Dictionary<string, ProviderPolicy>(StringComparer.Ordinal)
    {
        ["documents"] = new(SourceAuthority.AuthorizedArchive, "household"),
        // Documents saved before save_document was generalized; read like documents.
        ["household-documents"] = new(SourceAuthority.AuthorizedArchive, "household"),
        // Entries a dashboard's declared log writes add.
        ["log"] = new(SourceAuthority.AuthorizedArchive, "household"),
    };
    // Provider names no adapter or plugin may register: 'memory' is the native memories' (migration 025, whose guards
    // refuse the engine's login any other writer's rows under it).
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) { "memory" };
    // After Core: static initializers run in order.
    public static ProviderPolicies Default { get; } = new();
    public ProviderPolicies(IReadOnlyDictionary<string, ProviderPolicy>? additionalAdapters = null)
    {
        var registered = new Dictionary<string, ProviderPolicy>(Core, StringComparer.Ordinal);
        foreach (var entry in additionalAdapters ?? new Dictionary<string, ProviderPolicy>())
        {
            if (Reserved.Contains(entry.Key)) throw new ArgumentException("Provider name reserved for native memories");
            // Extensions cannot override the security policy of an existing adapter.
            if (!registered.TryAdd(entry.Key, entry.Value)) throw new ArgumentException("Adapter already registered");
        }
        policies = registered;
    }
    public IReadOnlyCollection<string> Providers => policies.Keys.ToArray();
    public string[] ArchiveProviders(string domain)=>policies.Where(p=>p.Value.Authority==SourceAuthority.AuthorizedArchive && p.Value.DefaultDomain==domain).Select(p=>p.Key).ToArray();

    public ProviderPolicy For(string provider) => policies.TryGetValue(provider, out var value)
        ? value : new(SourceAuthority.LiveSource); // Unknown providers never become trusted archives.

    public string Validate(ResourceInput resource)
    {
        var policy = For(resource.Provider);
        if (policy.IndividualPrivateOnly && (resource.Scope != "private" || resource.Subject == "household"))
            throw new ArgumentException("Adapter requires an individual subject and private scope");
        if (policy.Authority == SourceAuthority.ReferenceOnly && !string.IsNullOrEmpty(resource.Content))
            throw new ArgumentException("Reference adapters cannot cache source content");
        var domain = resource.Domain ?? policy.DefaultDomain;
        if (!Regex.IsMatch(domain, "\\A[a-z][a-z0-9-]{0,63}\\z")) throw new ArgumentException("Invalid domain");
        return domain;
    }
}
