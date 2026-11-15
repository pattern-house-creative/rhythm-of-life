using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knowledge.Adapters.Drive;

// `rhythm connect drive [read|edit]`: signs this install in to Google with the install's own Google Cloud project
// (docs/adapters/drive.md). The CLI has already written the OAuth client (secrets/adapters/drive_oauth_client) from the
// descriptor's prompt. For each grant this prints Google's consent address; the person opens it in a browser on any
// computer and allows access. Google then sends that browser to a loopback address where nothing listens, so the page
// doesn't load: the person copies the whole address from the address bar and pastes it here. The code in it is
// exchanged (PKCE, checked state) for a refresh token, written as secrets/adapters/drive_<grant> with mode 0600.
// Read is Drive read-only; edit (only when adapters.drive.edits is on) is Sheets and Docs edits plus Drive metadata.
public sealed partial class DriveAdapter
{
    public const string RedirectUri = "http://127.0.0.1:53682/";

    public async Task<int> Connect(ConnectContext context, CancellationToken ct)
    {
        var output = context.Output;
        var mode = context.Args.FirstOrDefault();
        if (mode is not (null or "read" or "edit"))
        {
            output.WriteLine("Usage: rhythm connect drive [read|edit]   (no argument: read, then edit when adapters.drive.edits is on)");
            return 2;
        }
        var edits = context.Settings.Bool("edits", false);
        if (mode == "edit" && !edits)
        {
            output.WriteLine("Edits are off for Drive. Set adapters.drive.edits to true in rhythm.yaml first, then run this again.");
            return 2;
        }
        var clientText = GoogleApi.ReadSecret(Path.Combine(context.SecretsDir, $"{AdapterName}_{GoogleApi.Client}"))
            ?? GoogleApi.ReadSecret(context.Settings.SecretFile(GoogleApi.Client));
        if (clientText is null || OAuthClient.Parse(clientText) is not { } client)
        {
            output.WriteLine($"The OAuth client is missing or not the JSON Google Cloud gives for a Desktop app client. Save it as "
                + $"secrets/adapters/{AdapterName}_{GoogleApi.Client} (see docs/adapters/drive.md) and run this again.");
            return 1;
        }
        using var http = AdapterHttp.Client(handler, TimeSpan.FromSeconds(30));
        string? readAccount = null;
        if (mode is null or "read")
        {
            if (await Grant(context, http, client, GoogleApi.ReadGrant, GoogleApi.ReadScope, "read your Google Drive (read only)", null, ct) is not { } account)
                return 1;
            readAccount = account;
        }
        if (mode == "edit" || mode is null && edits)
            if (await Grant(context, http, client, GoogleApi.EditGrant, GoogleApi.EditScope, "change the Sheets and Docs your dashboards write to", readAccount, ct) is null)
                return 1;
        output.WriteLine("Done. The engine uses the new access on its next Drive call; no restart is needed.");
        return 0;
    }

    // One consent: the Google account's permission id when the grant was saved, else null (and why, on the output).
    async Task<string?> Grant(ConnectContext context, HttpClient http, OAuthClient client, string grant, string scope, string what,
        string? sameAccountAs, CancellationToken ct)
    {
        var output = context.Output;
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var consent = GoogleApi.ConsentUrl + "?" + string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = client.Id, ["redirect_uri"] = RedirectUri, ["response_type"] = "code", ["scope"] = scope,
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        output.WriteLine();
        output.WriteLine($"To let Rhythm of Life {what}, open this address in a browser (on any computer) and sign in with the Google account that holds the files:");
        output.WriteLine();
        output.WriteLine(consent);
        output.WriteLine();
        output.WriteLine($"After you allow access, the browser goes to an address that starts with {RedirectUri} and the page doesn't load. That is expected.");
        output.WriteLine("Copy the whole address from the browser's address bar and paste it here.");
        output.Write("Address: ");
        output.Flush();
        var pasted = (await context.Input.ReadLineAsync(ct))?.Trim();
        if (!Uri.TryCreate(pasted, UriKind.Absolute, out var back) || back.GetLeftPart(UriPartial.Authority) + "/" != RedirectUri)
        {
            output.WriteLine($"That isn't the address Google sent the browser to (it starts with {RedirectUri}). Nothing was saved; run this again.");
            return null;
        }
        var query = back.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .GroupBy(p => WebUtility.UrlDecode(p[0])).ToDictionary(g => g.Key, g => g.Count() == 1 && g.First().Length == 2 ? WebUtility.UrlDecode(g.First()[1]) : null);
        if (query.GetValueOrDefault("state") is not { } returned || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returned), Encoding.ASCII.GetBytes(state)))
        {
            output.WriteLine("That address belongs to another sign-in attempt. Nothing was saved; run this again and use the newest address.");
            return null;
        }
        if (query.GetValueOrDefault("error") is { } error)
        {
            output.WriteLine(error == "access_denied" ? "Access wasn't allowed on Google's page. Nothing was saved."
                : "Google refused the sign-in. Nothing was saved; check the consent screen setup in docs/adapters/drive.md.");
            return null;
        }
        if (query.GetValueOrDefault("code") is not { Length: > 0 and <= 2000 } code)
        {
            output.WriteLine("That address has no sign-in code. Nothing was saved; run this again.");
            return null;
        }
        try
        {
            using var exchange = new HttpRequestMessage(HttpMethod.Post, GoogleApi.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code, ["client_id"] = client.Id, ["client_secret"] = client.Secret, ["code_verifier"] = verifier,
                    ["redirect_uri"] = RedirectUri, ["grant_type"] = "authorization_code"
                })
            };
            using var response = await http.SendAsync(exchange, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await AdapterHttp.Read(response, 64 * 1024, ct);
            if (!response.IsSuccessStatusCode)
            {
                output.WriteLine("Google didn't accept the code (it is valid for a few minutes and only once). Nothing was saved; run this again.");
                return null;
            }
            using var token = JsonDocument.Parse(body);
            var refresh = token.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var access = token.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            var granted = (token.RootElement.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (scope.Split(' ').Any(wanted => !granted.Contains(wanted)))
            {
                output.WriteLine("Google granted less than Rhythm asked for: tick every box on the consent page. Nothing was saved; run this again.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(refresh) || refresh.Length > 2000 || string.IsNullOrWhiteSpace(access))
            {
                output.WriteLine("Google gave no lasting access. Remove this app's access in your Google account's security settings, then run this again.");
                return null;
            }
            using var about = new HttpRequestMessage(HttpMethod.Get, $"{GoogleApi.DriveUrl}/about?fields=user(emailAddress,permissionId)");
            about.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var who = await http.SendAsync(about, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!who.IsSuccessStatusCode)
            {
                output.WriteLine(GoogleApi.Disabled(await AdapterHttp.Read(who, 16 * 1024, ct))
                    ? "The Google Drive API isn't switched on in your Google Cloud project yet. Switch it on (docs/adapters/drive.md) and run this again."
                    : "Google didn't confirm which account signed in. Nothing was saved; run this again.");
                return null;
            }
            using var user = JsonDocument.Parse(await AdapterHttp.Read(who, 16 * 1024, ct));
            var account = user.RootElement.GetProperty("user");
            var permission = account.TryGetProperty("permissionId", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(permission))
            {
                output.WriteLine("Google didn't confirm which account signed in. Nothing was saved; run this again.");
                return null;
            }
            if (sameAccountAs is not null && permission != sameAccountAs)
            {
                output.WriteLine("That was a different Google account from the one that allowed reading. Nothing was saved for edits; "
                    + "run rhythm connect drive edit and sign in with the same account.");
                return null;
            }
            WriteSecret(context.SecretsDir, $"{AdapterName}_{grant}", refresh);
            var email = account.TryGetProperty("emailAddress", out var m) ? m.GetString() : null;
            output.WriteLine($"Saved secrets/adapters/{AdapterName}_{grant}{(email is { Length: > 0 } ? $" for {email}" : "")}.");
            return permission;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException
            || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            output.WriteLine("Google couldn't be reached or gave an unexpected answer. Nothing was saved; run this again.");
            return null;
        }
    }

    // Written whole or not at all, readable by its owner only.
    static void WriteSecret(string directory, string name, string value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = path + ".new";
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options)) stream.Write(Encoding.UTF8.GetBytes(value));
        File.Move(temporary, path, true);
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
