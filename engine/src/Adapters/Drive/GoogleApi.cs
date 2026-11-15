using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.Adapters.Drive;

// Google's APIs for the drive adapter, with the install's own Google Cloud project: the OAuth client
// (secrets/adapters/drive_oauth_client, the JSON Google Cloud gives for a Desktop app) and a refresh token per grant,
// drive_read_token (Drive read-only) and drive_edit_token (Sheets and Docs edits, only when adapters.drive.edits is on).
// Access tokens live in memory for a few minutes; a replaced client or grant file takes effect on the next call.
sealed class GoogleApi(AdapterSettings settings, HttpClient http)
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token", ConsentUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string DriveUrl = "https://www.googleapis.com/drive/v3", SheetsUrl = "https://sheets.googleapis.com/v4/spreadsheets",
        DocsUrl = "https://docs.googleapis.com/v1/documents";
    public const string ReadGrant = "read_token", EditGrant = "edit_token", Client = "oauth_client";
    // Read: every file's metadata and contents, never a change. Edit: Sheets and Docs, plus Drive metadata for revisions.
    public const string ReadScope = "https://www.googleapis.com/auth/drive.readonly";
    public const string EditScope = "https://www.googleapis.com/auth/spreadsheets https://www.googleapis.com/auth/documents "
        + "https://www.googleapis.com/auth/drive.metadata.readonly";

    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, (string Hash, string Token, DateTimeOffset Until)> cached = [];
    // Why a grant last failed; read outside the gate by concurrent feeds.
    readonly ConcurrentDictionary<string, string> problems = new();

    public HttpClient Http => http;

    // A short-lived access token for one grant, or null when that grant is missing, expired or revoked (Problem says which).
    public async Task<string?> AccessToken(string grant, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var client = ReadSecret(settings.SecretFile(Client));
            var refresh = ReadSecret(settings.SecretFile(grant));
            if (client is null || refresh is null)
            {
                problems[grant] = client is null ? "Google Drive isn't set up yet: add your OAuth client and run rhythm connect drive."
                    : grant == EditGrant ? "Edit access to Google isn't set up yet: turn on adapters.drive.edits and run rhythm connect drive edit."
                    : "Google Drive isn't connected yet: run rhythm connect drive.";
                return null;
            }
            var hash = Hashing.Hash(client + "\n" + refresh);
            if (cached.TryGetValue(grant, out var kept) && kept.Hash == hash && DateTimeOffset.UtcNow < kept.Until) return kept.Token;
            cached.Remove(grant);
            if (OAuthClient.Parse(client) is not { } oauth)
            {
                problems[grant] = "The OAuth client file isn't the JSON Google Cloud gives for a Desktop app.";
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = oauth.Id, ["client_secret"] = oauth.Secret
                })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await AdapterHttp.Read(response, 64 * 1024, ct);
            if (!response.IsSuccessStatusCode)
            {
                // invalid_grant: expired (after 7 days while the Google Cloud app is in testing) or revoked.
                problems[grant] = Encoding.UTF8.GetString(body).Contains("invalid_grant", StringComparison.Ordinal)
                    ? "Google no longer accepts this sign-in (it expired or was revoked): run rhythm connect drive again."
                    : NotAnswering;
                return null;
            }
            using var json = JsonDocument.Parse(body);
            var token = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 0;
            if (string.IsNullOrWhiteSpace(token) || seconds <= 60)
            {
                problems[grant] = Unusable;
                return null;
            }
            problems.TryRemove(grant, out _);
            cached[grant] = (hash, token, DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds - 60, 3000)));
            return token;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            problems[grant] = NotAnswering;
            return null;
        }
        finally { gate.Release(); }
    }
    public string Problem(string grant) => problems.GetValueOrDefault(grant) ?? "Google Drive is unavailable right now.";
    // Whether a grant's last failure was Google's sign-in service not answering, or answering with no usable token (worth
    // trying again), rather than a grant or client file missing or unusable, or a sign-in expired or revoked (which stays
    // so until a person connects Drive again).
    public bool Outage(string grant) => problems.GetValueOrDefault(grant) is NotAnswering or Unusable;
    const string NotAnswering = "Google's sign-in service isn't answering right now.", Unusable = "Google's sign-in service gave an unusable answer.";

    // A secret file's value, or null when it is missing, empty, unreadable or a directory (Docker's stand-in for a
    // secret file that did not exist).
    public static string? ReadSecret(string? path)
    {
        if (path is null || Directory.Exists(path) || !File.Exists(path)) return null;
        try { return SecretFiles.Read(path) is { Length: > 0 } value ? value : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // One call with a bearer token: the status and the body, read up to `maximum` bytes (16 KiB for a failure).
    public async Task<(HttpStatusCode Status, byte[] Body)> Send(HttpMethod method, string url, string token, JsonNode? body,
        int maximum, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return (response.StatusCode, await AdapterHttp.Read(response, maximum, ct));
        try { return (response.StatusCode, await AdapterHttp.Read(response, 16 * 1024, ct)); }
        catch (IOException) { return (response.StatusCode, []); }
    }

    // Google says an API is switched off for the Cloud project when it answers SERVICE_DISABLED.
    public static bool Disabled(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.Contains("SERVICE_DISABLED", StringComparison.Ordinal) || text.Contains("has not been used in project", StringComparison.Ordinal);
    }
}

// The OAuth client a Google Cloud project gives for a Desktop app: {"installed": {"client_id", "client_secret", ...}}.
sealed record OAuthClient(string Id, string Secret)
{
    public static OAuthClient? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object) return null;
            var id = installed.TryGetProperty("client_id", out var i) ? i.GetString() : null;
            var secret = installed.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
            return id is { Length: > 0 and <= 300 } && id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
                && secret is { Length: > 0 and <= 300 } ? new(id, secret) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
}
