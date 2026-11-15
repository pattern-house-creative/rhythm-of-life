using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Garmin;

// Why a Garmin call did not succeed, as a short code (never a payload or a URL with data); RetryAt when Garmin asked
// to wait.
public sealed class GarminException(string code, DateTimeOffset? retryAt = null) : Exception(code)
{
    public string Code => Message;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
public sealed record GarminData(byte[] Bytes, string MediaType);

// Garmin's official Personal API with the person's own key: the key is exchanged for a short-lived access token
// (POST TokenPath with `Authorization: ApiKey <key>`), which then reads the data paths. One request at a time,
// spaced by `spacing`; a 429 or 5xx is retried with Garmin's own Retry-After (never shortened), and a delay longer
// than two minutes is handed back as RetryAt instead of waited for. The key is read from its file on every token
// exchange, so a replaced file takes effect without a restart.
public sealed partial class GarminClient(HttpClient http, Func<string?> readKey, TimeSpan? spacing = null,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    public const string Origin = "https://personal.connectapi.garmin.com";
    public const string TokenPath = "/personalapitoken-service/personal-api/personal-api-key/token";
    public const int MaxPayload = 16 * 1024 * 1024;
    readonly SemaphoreSlim gate = new(1);
    string? token;
    DateTimeOffset expires;
    readonly Func<TimeSpan, CancellationToken, Task> delay = wait ?? ((d, c) => Task.Delay(d, c));

    // One data path's body, or null when Garmin has nothing for it (204 or 404).
    public async Task<GarminData?> Get(string path, CancellationToken ct = default)
    {
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\') || path.Contains("..")) throw new ArgumentException("Invalid provider path");
        await gate.WaitAsync(ct);
        try
        {
            for (var auth = 0; auth < 2; auth++)
            {
                if (token is null || expires <= DateTimeOffset.UtcNow) await Authenticate(ct);
                using var result = await Send(path, false, ct);
                if (result.StatusCode == HttpStatusCode.Unauthorized) { token = null; continue; }
                if (result.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
                if (!result.IsSuccessStatusCode) throw new GarminException($"garmin_http_{(int)result.StatusCode}");
                return new(await ReadBounded(result, MaxPayload, ct), result.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
            }
            throw new GarminException("garmin_credentials_rejected");
        }
        finally { gate.Release(); }
    }

    // Garmin answers the exchange with a compact JWT as text/plain; a JSON body with access_token (and expires_in)
    // is accepted too. Any other body is never taken for a token.
    async Task Authenticate(CancellationToken ct)
    {
        var acquired = DateTimeOffset.UtcNow;
        using var response = await Send(TokenPath, true, ct);
        if (!response.IsSuccessStatusCode) throw new GarminException("garmin_credentials_rejected");
        var bytes = await ReadBounded(response, 64 * 1024, ct);
        var lifetime = 86400d;
        if (response.Content.Headers.ContentType?.MediaType == "text/plain")
        {
            var plain = Encoding.UTF8.GetString(bytes).Trim();
            if (!Jwt().IsMatch(plain)) throw new GarminException("garmin_token_contract_unverified");
            token = plain;
        }
        else
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("access_token", out var access)
                    || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()))
                    throw new GarminException("garmin_token_contract_unverified");
                token = access.GetString();
                if (json.RootElement.TryGetProperty("expires_in", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                    && seconds.TryGetDouble(out var n) && double.IsFinite(n))
                    lifetime = Math.Min(n, 86400);
            }
            catch (JsonException) { throw new GarminException("garmin_token_contract_unverified"); }
        }
        expires = acquired.AddSeconds(Math.Max(0, lifetime - 300));
    }

    async Task<HttpResponseMessage> Send(string path, bool authentication, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await delay(spacing ?? TimeSpan.FromSeconds(1), ct);
            var key = authentication ? readKey()?.Trim() : null;
            if (authentication && string.IsNullOrEmpty(key)) throw new GarminException("garmin_credentials_missing");
            using var request = new HttpRequestMessage(authentication ? HttpMethod.Post : HttpMethod.Get, Origin + path);
            request.Headers.Authorization = new AuthenticationHeaderValue(authentication ? "ApiKey" : "Bearer", authentication ? key : token);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) when (attempt < 4) { await delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); continue; }
            catch (HttpRequestException) { throw new GarminException("garmin_unreachable"); }
            if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) return response;
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
            response.Dispose();
            if (retry > TimeSpan.FromMinutes(2)) throw new GarminException("garmin_rate_limit_defer", DateTimeOffset.UtcNow.Add(retry));
            if (attempt >= 4) throw new GarminException("garmin_retry_exhausted", DateTimeOffset.UtcNow.Add(retry));
            await delay(retry < TimeSpan.Zero ? TimeSpan.Zero : retry, ct);
        }
    }

    public static async Task<byte[]> ReadBounded(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new GarminException("provider_payload_too_large");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maximum) throw new GarminException("provider_payload_too_large");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex Jwt();
}
