using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Knowledge;

public sealed class OAuthServer(RhythmConfig config, Database db)
{
    readonly OAuthSettings settings = OAuthSettings.From(config)!;
    public string Resource { get; } = config.Engine.PublicMcpUrl!;
    public string Issuer { get; } = new Uri(config.Engine.PublicMcpUrl!).GetLeftPart(UriPartial.Authority);
    public string Challenge => $"Bearer resource_metadata=\"{Issuer}/.well-known/oauth-protected-resource\", scope=\"{OAuthSettings.Scope}\"";

    // Cover case and trailing-slash variants the framework can recognize before OpenIddict handles them.
    public static bool ProtocolPath(PathString path) => path.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase);

    public static void Add(IServiceCollection services, RhythmConfig config, Database db, string data)
    {
        var settings = OAuthSettings.From(config)!;
        var issuer = new Uri(config.Engine.PublicMcpUrl!).GetLeftPart(UriPartial.Authority);
        var keys = Path.Combine(data, "oauth-keys");
        Directory.CreateDirectory(keys);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        services.AddSingleton(new OAuthServer(config, db));
        services.AddDbContext<OAuthDb>(o => { o.UseNpgsql(db.Source); o.UseOpenIddict(); });
        services.AddDataProtection().SetApplicationName("Rhythm.OAuth").PersistKeysToFileSystem(new DirectoryInfo(keys))
            .ProtectKeysWithCertificate(Certificate(keys, "protection"));
        services.AddAntiforgery(o => { o.Cookie.Name = "__Host-rhythm-oauth"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.HttpOnly = true; });
        services.AddAuthentication();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => ProtocolPath(c.Request.Path)
                ? RateLimitPartition.GetFixedWindowLimiter("oauth", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
        });
        services.Configure<FormOptions>(o => { o.ValueCountLimit = 32; o.ValueLengthLimit = 4096; o.KeyLengthLimit = 128; });
        services.AddOpenIddict().AddCore(o => { o.UseEntityFrameworkCore().UseDbContext<OAuthDb>(); o.DisableEntityCaching(); })
            .AddServer(o =>
            {
                o.SetIssuer(new Uri(issuer));
                o.SetConfigurationEndpointUris("/.well-known/oauth-authorization-server");
                o.SetAuthorizationEndpointUris("/oauth/authorize").SetTokenEndpointUris("/oauth/token")
                    .SetRevocationEndpointUris("/oauth/revoke").SetJsonWebKeySetEndpointUris("/.well-known/jwks");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                o.Configure(options =>
                {
                    options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                    options.Scopes.Remove(Scopes.OpenId);
                });
                o.RegisterScopes(OAuthSettings.Scope);
                o.RegisterResources(config.Engine.PublicMcpUrl!);
                o.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15)).SetRefreshTokenLifetime(TimeSpan.FromDays(30))
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
                o.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                o.AddSigningCertificate(Certificate(keys, "signing")).AddEncryptionCertificate(Certificate(keys, "encryption"));
                // TLS terminates at the configured reverse proxy. ProtocolGate still requires the exact public host;
                // no forwarded header chooses issuer, resource, redirect or identity.
                o.UseAspNetCore().DisableTransportSecurityRequirement().EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                o.AddEventHandler<OpenIddictServerEvents.ApplyConfigurationResponseContext>(b => b.UseInlineHandler(c =>
                {
                    c.Response["authorization_response_iss_parameter_supported"] = true;
                    // The sole registered client is public and uses PKCE without a client secret.
                    c.Response["token_endpoint_auth_methods_supported"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { ClientAuthenticationMethods.None });
                    return default;
                }));
                o.AddEventHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>(b => b.UseInlineHandler(c =>
                {
                    c.Response["iss"] = new Uri(issuer).AbsoluteUri;
                    return default;
                }));
            })
            .AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); o.AddAudiences(config.Engine.PublicMcpUrl!);
                o.EnableTokenEntryValidation(); o.EnableAuthorizationEntryValidation(); });
    }

    static X509Certificate2 Certificate(string folder, string name)
    {
        var file = Path.Combine(folder, name + ".pfx");
        if (!File.Exists(file))
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=Rhythm OAuth " + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            // CreateNew avoids replacing another process's key. Keys survive restart and are part of engine backups.
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(certificate.Export(X509ContentType.Pfx));
        }
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public async Task Initialize(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = settings.ClientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
            DisplayName = "ChatGPT", Permissions = { Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + OAuthSettings.Scope, Permissions.Prefixes.Resource + Resource },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };
        foreach (var redirect in settings.RedirectUris) descriptor.RedirectUris.Add(new Uri(redirect));
        var existing = await manager.FindByClientIdAsync(settings.ClientId, ct);
        if (existing is null) await manager.CreateAsync(descriptor, ct);
        else await manager.UpdateAsync(existing, descriptor, ct);
    }

    public async Task<Caller?> Caller(Guid id, CancellationToken ct)
    {
        await using var command = db.Source.CreateCommand($"""
            UPDATE principals p SET last_used_at=greatest(p.last_used_at,clock_timestamp())
            WHERE p.id=@id AND p.role='harness' AND {Principals.TokenCurrent} AND {Principals.PersonCurrent}
            RETURNING p.id,p.name,p.role
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), null) : null;
    }

    public async Task<Caller?> Authenticate(HttpContext context)
    {
        var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is not { } principal || !principal.HasScope(OAuthSettings.Scope)
            || !principal.GetPresenters().Contains(settings.ClientId) || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var id)) return null;
        return await Caller(id, context.RequestAborted);
    }

    public async Task ProtocolGate(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
        // no-referrer makes Chromium send Origin: null on the consent form's POST. Keep its same-origin
        // proof while suppressing the referrer when the authorization response leaves for the client.
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        var callbacks = string.Join(" ", settings.RedirectUris.Select(r => new Uri(r).GetLeftPart(UriPartial.Authority)).Distinct());
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; form-action 'self' {callbacks}; frame-ancestors 'none'; base-uri 'none'";
        if (!string.Equals(context.Request.Host.Value, new Uri(Issuer).Authority, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Headers.TryGetValue("Origin", out var origin)
            && (context.Request.Path != "/oauth/authorize" || origin.Count != 1 || origin[0] != Issuer))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.ContentLength > 16 * 1024) { context.Response.StatusCode = 413; return; }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 16 * 1024;
        // The configured HTTPS reverse proxy terminates TLS; use its fixed public scheme for the secure
        // antiforgery cookie, never a caller-supplied forwarded header. The engine port stays private.
        context.Request.Scheme = "https";
        await next(context);
    }

    public void Use(WebApplication app, Func<bool> ready)
    {
        app.UseRateLimiter();
        // OpenIddict handles discovery itself. Check the public boundary before its middleware.
        app.Use(async (context, next) =>
        {
            if (!ProtocolPath(context.Request.Path)) { await next(context); return; }
            if (!ready()) { context.Response.StatusCode = 503; return; }
            await ProtocolGate(context, next);
        });
        app.UseAuthentication();
    }

    public void Map(WebApplication app)
    {
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
            app.MapGet(path, () => Results.Json(new { resource = Resource, authorization_servers = new[] { new Uri(Issuer).AbsoluteUri },
                scopes_supported = new[] { OAuthSettings.Scope }, bearer_methods_supported = new[] { "header" } }));
        app.MapMethods("/oauth/authorize", ["GET", "POST"], (Delegate)Authorize);
        app.MapPost("/oauth/token", (Delegate)Exchange);
    }

    async Task<IResult> Authorize(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OAuth request missing");
        if (!ResourceMatches(request)) return OAuthError(Errors.InvalidTarget);
        if (!request.GetScopes().Contains(OAuthSettings.Scope)) return OAuthError(Errors.InvalidScope);
        var csrf = context.RequestServices.GetRequiredService<IAntiforgery>();
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var token = csrf.GetAndStoreTokens(context);
            var fields = string.Join("", request.GetParameters().Where(p => p.Key is not "request" and not "request_uri")
                .Select(p => Hidden(p.Key, p.Value.ToString() ?? "")));
            return Results.Content($$"""
                <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                <title>Connect ChatGPT to Rhythm</title><style>body{font:18px system-ui;max-width:34rem;margin:4rem auto;padding:1rem;background:#101a20;color:#eef5f4}input,button{font:inherit;padding:.7rem;box-sizing:border-box}input[type=password]{width:100%}button{margin-top:1rem}p{line-height:1.5}</style>
                <h1>Connect ChatGPT to Rhythm</h1><p>ChatGPT will be able to read the data available to this Rhythm token and use its supported write tools, including memories, insights, dashboards and screen controls.</p>
                <p>Use a dedicated harness token made for this connection. Your token stays with Rhythm and is not sent to ChatGPT. Revoke that token to end this connection.</p>
                <form method="post" action="/oauth/authorize">{{fields}}{{Hidden(token.FormFieldName, token.RequestToken!)}}
                <label for="credential">Rhythm connection token</label><input id="credential" name="credential" type="password" required autocomplete="off" maxlength="512">
                <button type="submit">Allow ChatGPT access</button></form></html>
                """, "text/html; charset=utf-8");
        }
        if (!await csrf.IsRequestValidAsync(context)) return Results.StatusCode(400);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var credential = form["credential"].ToString();
        if (credential.Length is < 32 or > 512) return Results.StatusCode(401);
        var caller = await db.Authenticate(credential, context.RequestAborted);
        if (caller is not { Role: "harness" }) return Results.StatusCode(401);
        Mcp.RequestLog.Of(context)?.Authenticated(caller, "oauth_authorize");
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(Claims.Subject, caller.Id.ToString());
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(Resource);
        foreach (var claim in principal.Claims) claim.SetDestinations(Destinations.AccessToken);
        var applications = context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(settings.ClientId, context.RequestAborted) ?? throw new InvalidOperationException("OAuth client missing");
        var authorizations = context.RequestServices.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.CreateAsync(principal, caller.Id.ToString(),
            (await applications.GetIdAsync(application, context.RequestAborted))!, AuthorizationTypes.Permanent, principal.GetScopes(), context.RequestAborted);
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization, context.RequestAborted));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    async Task<IResult> Exchange(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OAuth request missing");
        if (!ResourceMatches(request)) return OAuthError(Errors.InvalidTarget);
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is not { } principal
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var id) || await Caller(id, context.RequestAborted) is not { } caller)
            return OAuthError(Errors.InvalidGrant);
        Mcp.RequestLog.Of(context)?.Authenticated(caller, "oauth_token");
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    bool ResourceMatches(OpenIddictRequest request) => request.GetResources() is { Length: 1 } resources && resources[0] == Resource;
    static IResult OAuthError(string error) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        { [OpenIddictServerAspNetCoreConstants.Properties.Error] = error }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{HtmlEncoder.Default.Encode(name)}\" value=\"{HtmlEncoder.Default.Encode(value)}\">";
}
