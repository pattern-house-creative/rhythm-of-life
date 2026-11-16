namespace Knowledge;

// Optional, additive OAuth for hosted clients that cannot supply a fixed header. The existing MCP URL and
// every existing principal remain unchanged. Client registration is explicit, not open dynamic registration.
public sealed class OAuthSettings
{
    public bool Enabled { get; init; }
    public string ClientId { get; init; } = "rhythm-chatgpt";
    public List<string> RedirectUris { get; init; } = [];
    public const string Scope = "rhythm";

    public static OAuthSettings? From(RhythmConfig config)
    {
        var settings = config.Engine.Oauth;
        if (settings is not { Enabled: true }) return null;
        if (!Uri.TryCreate(config.Engine.PublicMcpUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.AbsolutePath != "/mcp" || url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0)
            throw new ConfigException("engine.oauth requires an HTTPS engine.publicMcpUrl ending in exactly /mcp");
        if (!config.Engine.AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase)
            && !config.Engine.AllowedHosts.Contains(url.Authority, StringComparer.OrdinalIgnoreCase))
            throw new ConfigException("engine.oauth requires the public MCP host in engine.allowedHosts");
        if (string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId.Length > 100
            || settings.ClientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ConfigException("engine.oauth.clientId must be 1-100 ASCII letters, digits, hyphens or underscores");
        if (settings.RedirectUris.Count is < 1 or > 10 || settings.RedirectUris.Any(r =>
            !Uri.TryCreate(r, UriKind.Absolute, out var redirect) || redirect.Scheme != "https"
            || redirect.UserInfo.Length != 0 || redirect.Fragment.Length != 0 || redirect.Query.Length != 0 || r.Contains('*')))
            throw new ConfigException("engine.oauth.redirectUris must list 1-10 exact HTTPS callback URLs, without queries, fragments or wildcards");
        return settings;
    }
}
