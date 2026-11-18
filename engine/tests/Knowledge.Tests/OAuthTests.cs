using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace Knowledge.Tests;

public sealed class OAuthConfigTests
{
    [Fact] public void OAuth_is_opt_in_and_requires_canonical_https_and_exact_callbacks()
    {
        Assert.Null(OAuthSettings.From(new()));
        Assert.Throws<ConfigException>(() => OAuthSettings.From(new() { Engine = new() { Oauth = new() { Enabled = true } } }));
        foreach (var callback in new[] { "http://client.example.invalid/callback", "https://*.example.invalid/callback", "https://client.example.invalid/callback#fragment" })
            Assert.Throws<ConfigException>(() => OAuthSettings.From(Config(callback)));
        Assert.NotNull(OAuthSettings.From(Config()));
    }
    internal static RhythmConfig Config(string callback = "https://client.example.invalid/callback") => new()
    {
        Engine = new() { PublicMcpUrl = "https://mcp.example.invalid/mcp", AllowedHosts = ["mcp.example.invalid", "engine"],
            HostRoles = [new() { Host = "mcp.example.invalid", Roles = ["harness"] }],
            Oauth = new() { Enabled = true, RedirectUris = [callback] } }
    };
}

[Collection(SharedStateCollection.Name)]
public sealed class OAuthDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    const string Origin = "https://mcp.example.invalid", Resource = Origin + "/mcp", Redirect = "https://client.example.invalid/callback";
    sealed class Server(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public IServiceProvider Services => app.Services;
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }

    async Task<Server> Open(string directory, bool enabled = true)
    {
        var config = OAuthConfigTests.Config();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (enabled) OAuthServer.Add(builder.Services, config, fixture.Db, directory);
        var app = builder.Build();
        var oauth = app.Services.GetService<OAuthServer>();
        if (oauth is not null) { await oauth.Initialize(app.Services, default); oauth.Use(app, () => true); }
        app.Use(new Perimeter(config.Engine.AllowedHosts, () => true, (t, ct) => fixture.Db.Authenticate(t, ct),
            new(App: (_, _) => Task.CompletedTask, Ingest: (_, _) => Task.CompletedTask, Document: (_, _, _) => Task.CompletedTask), oauth: oauth,
            hostAcceptsRole: config.Engine.AcceptsRole).Handle);
        oauth?.Map(app);
        app.MapPost("/mcp", (HttpContext c) => Results.Text(c.Items["principal"]!.ToString()!));
        await app.StartAsync();
        // TLS terminates at the proxy; the engine receives HTTP with the configured public Host.
        var client = app.GetTestClient(); client.BaseAddress = new Uri("http://mcp.example.invalid");
        return new(app, client);
    }

    static string Temp() => Directory.CreateTempSubdirectory("rhythm-oauth-test-").FullName;
    async Task<(string Name, string Token, Guid Id)> Credential(string directory, string role = "harness")
    {
        var name = "oauth-test-" + Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(directory, name);
        var id = await new Principals(fixture.Source).Create(name, role, role == "ingest" ? "fixture" : null, path, default);
        return (name, await File.ReadAllTextAsync(path), id);
    }
    static async Task<HttpResponseMessage> Mcp(HttpClient client, string? token = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    static string Verifier() => Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    static Dictionary<string, string?> Parameters(string verifier) => new()
    {
        ["client_id"] = "rhythm-chatgpt", ["response_type"] = "code", ["redirect_uri"] = Redirect,
        ["scope"] = "rhythm offline_access", ["resource"] = Resource, ["state"] = "synthetic-state",
        ["code_challenge"] = Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256"
    };
    static async Task<HttpResponseMessage> Consent(HttpClient client, string credential, Dictionary<string, string?> parameters,
        bool csrf = true, string? origin = Origin)
    {
        using var page = await client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", parameters));
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.IsSuccessStatusCode, html);
        Assert.Equal("same-origin", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("https://client.example.invalid", page.Headers.GetValues("Content-Security-Policy").Single());
        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
        if (!csrf) fields.Remove("__RequestVerificationToken");
        fields["credential"] = credential;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Cookie", string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    static async Task<string> Code(HttpClient client, string credential, string verifier)
    {
        using var response = await Consent(client, credential, Parameters(verifier));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, body);
        var uri = response.Headers.Location!;
        Assert.StartsWith(Redirect + "?", uri.ToString());
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(Origin + "/", query["iss"].ToString());
        Assert.Equal("synthetic-state", query["state"].ToString());
        Assert.False(query.ContainsKey("credential"));
        return query["code"].ToString();
    }
    static Task<HttpResponseMessage> Exchange(HttpClient client, string code, string verifier, string resource = Resource, string redirect = Redirect) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["client_id"] = "rhythm-chatgpt", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = resource }));
    static Task<HttpResponseMessage> Refresh(HttpClient client, string token, string resource = Resource) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = "rhythm-chatgpt", ["refresh_token"] = token, ["resource"] = resource }));
    static async Task<JsonElement> TokenResponse(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }

    [PostgresFact] public async Task Existing_bearer_and_oauth_share_mcp_without_sharing_credentials_and_survive_restart()
    {
        var directory = Temp();
        var credential = await Credential(directory);
        var legacy = await Credential(directory);
        string access, refresh;
        await using (var server = await Open(directory))
        {
            var client = server.Client;
            using var original = await Mcp(client, legacy.Token);
            Assert.Equal(legacy.Id.ToString(), await original.Content.ReadAsStringAsync());
            using var anonymous = await Mcp(client);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Contains("oauth-protected-resource", anonymous.Headers.WwwAuthenticate.ToString());
            using var discovery = await client.GetAsync("/.well-known/oauth-authorization-server");
            var metadata = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(Origin + "/", metadata.GetProperty("issuer").GetString());
            Assert.True(metadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
            Assert.Equal(new[] { "none" }, metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(v => v.GetString()));
            Assert.DoesNotContain("openid", metadata.GetProperty("scopes_supported").EnumerateArray().Select(v => v.GetString()));
            using var oidc = await client.GetAsync("/.well-known/openid-configuration");
            Assert.Equal(HttpStatusCode.NotFound, oidc.StatusCode);
            var verifier = Verifier();
            var code = await Code(client, credential.Token, verifier);
            var tokens = await TokenResponse(await Exchange(client, code, verifier));
            access = tokens.GetProperty("access_token").GetString()!;
            refresh = tokens.GetProperty("refresh_token").GetString()!;
            Assert.NotEqual(credential.Token, access);
            using var oauth = await Mcp(client, access);
            Assert.Equal(credential.Id.ToString(), await oauth.Content.ReadAsStringAsync());
            using var browser = await Mcp(client, access, Origin);
            Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
            using var reusedCode = await Exchange(client, code, verifier);
            Assert.Equal(HttpStatusCode.BadRequest, reusedCode.StatusCode);
        }
        await using (var server = await Open(directory))
        {
            // Reusing a code can revoke its grant. Get a fresh grant for the restart/refresh case.
            var verifier = Verifier();
            var tokens = await TokenResponse(await Exchange(server.Client, await Code(server.Client, credential.Token, verifier), verifier));
            access = tokens.GetProperty("access_token").GetString()!;
            refresh = tokens.GetProperty("refresh_token").GetString()!;
        }
        await using (var server = await Open(directory))
        {
            using var survived = await Mcp(server.Client, access);
            Assert.Equal(HttpStatusCode.OK, survived.StatusCode);
            var renewed = await TokenResponse(await Refresh(server.Client, refresh));
            Assert.NotEqual(refresh, renewed.GetProperty("refresh_token").GetString());
            access = renewed.GetProperty("access_token").GetString()!;
            refresh = renewed.GetProperty("refresh_token").GetString()!;
            await new Principals(fixture.Source).Revoke(credential.Name, default);
            using var revoked = await Mcp(server.Client, access);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var deniedRefresh = await Refresh(server.Client, refresh);
            Assert.Equal(HttpStatusCode.BadRequest, deniedRefresh.StatusCode);
            using var unchanged = await Mcp(server.Client, legacy.Token);
            Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        }
    }

    [PostgresFact] public async Task Expiry_of_the_underlying_credential_ends_existing_OAuth_access_and_refresh()
    {
        var directory = Temp();
        var credential = await Credential(directory);
        await using var server = await Open(directory);
        var verifier = Verifier();
        var code = await Code(server.Client, credential.Token, verifier);
        var grant = await TokenResponse(await Exchange(server.Client, code, verifier));
        var access = grant.GetProperty("access_token").GetString();
        var refresh = grant.GetProperty("refresh_token").GetString()!;
        await Scoped.Execute(fixture.Source, "UPDATE principals SET last_used_at=NULL WHERE id=@id", ("id", credential.Id));
        using var accepted = await Mcp(server.Client, access);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull((await new Principals(fixture.Source).List(default)).Single(t => t.Id == credential.Id).LastUsedAt);
        await Scoped.Execute(fixture.Source, "UPDATE principals SET expires_at=now()-interval '1 second' WHERE id=@id", ("id", credential.Id));
        using var denied = await Mcp(server.Client, access);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedRefresh = await Refresh(server.Client, refresh);
        Assert.Equal(HttpStatusCode.BadRequest, deniedRefresh.StatusCode);
    }

    [PostgresFact] public async Task OAuth_rejects_wrong_resource_pkce_callbacks_csrf_and_foreign_origin()
    {
        var directory = Temp();
        var credential = await Credential(directory);
        await using var server = await Open(directory);
        var client = server.Client;
        foreach (var change in new[] { ("redirect_uri", "https://evil.example.invalid/callback"), ("resource", "https://other.example.invalid/mcp"),
            ("code_challenge_method", "plain"), ("client_id", "unregistered"), ("scope", "other-scope") })
        {
            var parameters = Parameters(Verifier()); parameters[change.Item1] = change.Item2;
            using var response = await client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", parameters));
            // OAuth can redirect a protocol error only to an already validated callback.
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Redirect,
                change.Item1 + ": " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
            if (response.Headers.Location is { } location)
            { Assert.StartsWith(Redirect + "?", location.ToString()); Assert.Contains("error=", location.Query); }
        }
        using var noCsrf = await Consent(client, credential.Token, Parameters(Verifier()), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var foreign = await Consent(client, credential.Token, Parameters(Verifier()), origin: "https://evil.example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        var verifier = Verifier();
        using var wrongVerifier = await Exchange(client, await Code(client, credential.Token, verifier), Verifier());
        Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);
        verifier = Verifier();
        using var wrongResource = await Exchange(client, await Code(client, credential.Token, verifier), verifier, "https://other.example.invalid/mcp");
        Assert.Equal(HttpStatusCode.BadRequest, wrongResource.StatusCode);
        var ingest = await Credential(directory, "ingest");
        using var wrongRole = await Consent(client, ingest.Token, Parameters(Verifier()));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRole.StatusCode);
        using var noCredential = await Consent(client, Principals.NewToken(), Parameters(Verifier()));
        Assert.Equal(HttpStatusCode.Unauthorized, noCredential.StatusCode);
        using var wrongHost = await client.GetAsync("https://engine/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
    }

    [PostgresFact] public async Task OAuth_expiry_revocation_and_refresh_replay_are_enforced()
    {
        var directory = Temp();
        var credential = await Credential(directory);
        await using var server = await Open(directory);
        var client = server.Client;
        async Task<JsonElement> Grant()
        {
            var verifier = Verifier();
            return await TokenResponse(await Exchange(client, await Code(client, credential.Token, verifier), verifier));
        }
        var grant = await Grant();
        var access = grant.GetProperty("access_token").GetString()!;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.FindByReferenceIdAsync(access);
            Assert.NotNull(token);
            var descriptor = new OpenIddictTokenDescriptor();
            await tokens.PopulateAsync(descriptor, token);
            descriptor.ExpirationDate = DateTimeOffset.UtcNow.AddMinutes(-1);
            await tokens.UpdateAsync(token, descriptor);
        }
        using var expired = await Mcp(client, access);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        grant = await Grant();
        access = grant.GetProperty("access_token").GetString()!;
        using var revoke = await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = "rhythm-chatgpt", ["token"] = access, ["token_type_hint"] = "access_token" }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var revoked = await Mcp(client, access);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        grant = await Grant();
        access = grant.GetProperty("access_token").GetString()!;
        var refresh = grant.GetProperty("refresh_token").GetString()!;
        var renewed = await TokenResponse(await Refresh(client, refresh));
        using var replay = await Refresh(client, refresh);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using var revokedFamily = await Refresh(client, renewed.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, revokedFamily.StatusCode);

        grant = await Grant();
        access = grant.GetProperty("access_token").GetString()!;
        refresh = grant.GetProperty("refresh_token").GetString()!;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var token = (await tokens.FindByReferenceIdAsync(access))!;
            var authorization = (await authorizations.FindByIdAsync((await tokens.GetAuthorizationIdAsync(token))!))!;
            Assert.True(await authorizations.TryRevokeAsync(authorization));
        }
        using var denied = await Mcp(client, access);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedRefresh = await Refresh(client, refresh);
        Assert.Equal(HttpStatusCode.BadRequest, deniedRefresh.StatusCode);
        using var legacy = await Mcp(client, credential.Token);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    [PostgresFact] public async Task OAuth_protocol_endpoints_enforce_host_body_and_request_limits()
    {
        await using var server = await Open(Temp());
        using var wrongHost = await server.Client.GetAsync("https://engine/.well-known/oauth-authorization-server");
        Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
        using var caseVariant = await server.Client.GetAsync("https://engine/.WELL-KNOWN/oauth-authorization-server/");
        Assert.Equal(HttpStatusCode.Forbidden, caseVariant.StatusCode);
        using var oversized = await server.Client.PostAsync("/oauth/token", new StringContent(new string('x', 17000)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var malformed = await server.Client.PostAsync("/oauth/token", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        for (var i = 0; i < 120; i++)
        {
            using var response = await server.Client.GetAsync("/.well-known/oauth-protected-resource");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Fail("OAuth request limit was not enforced");
    }

    [PostgresFact] public async Task Disabled_oauth_keeps_bearer_authentication_and_has_no_protocol_routes()
    {
        var directory = Temp();
        var credential = await Credential(directory);
        await using var server = await Open(directory, enabled: false);
        using var bearer = await Mcp(server.Client, credential.Token);
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        using var anonymous = await Mcp(server.Client);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(anonymous.Headers.WwwAuthenticate);
        using var protocol = await server.Client.GetAsync("/.well-known/oauth-authorization-server");
        Assert.Equal(HttpStatusCode.NotFound, protocol.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(directory, "oauth-keys")));
    }
}
