namespace Knowledge.Adapters;

// A response past what its reader allows: an IOException as any failed read is, for a reader that says which it was.
public sealed class ResponseTooLargeException() : IOException("The response is larger than allowed");

// Outbound HTTP for adapters that call a vendor: no redirects, no cookies, a timeout, and bounded reads. A test passes
// its own handler (an in-process fake); nothing else reaches the network.
//
// RHYTHM_TEST_UPSTREAM is for tests of a whole stack only: when it is set, every call an adapter makes to
// https://<host>/<path> goes to <upstream>/<host>/<path> instead (a stub container serving fakes), so settings keep their
// real shape. Never set it in an install: the engine says so on its error output when it is set.
public static class AdapterHttp
{
    public const string TestUpstream = "RHYTHM_TEST_UPSTREAM";
    static int warned;

    // Connect: how an adapter that must see the address it reaches makes its connections; the handler's own way without one.
    public static HttpClient Client(HttpMessageHandler? handler, TimeSpan timeout, Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        if (handler is not null) return new HttpClient(handler, false) { Timeout = timeout };
        HttpMessageHandler inner = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectCallback = connect };
        if (Environment.GetEnvironmentVariable(TestUpstream) is { Length: > 0 } upstream)
        {
            if (Interlocked.Exchange(ref warned, 1) == 0)
                Console.Error.WriteLine($"{TestUpstream} is set: adapter calls go to a test stub, not to their services.");
            inner = new Upstream(new Uri(upstream.TrimEnd('/') + "/"), inner);
        }
        return new HttpClient(inner) { Timeout = timeout };
    }

    // The body, or an IOException past `maximum` bytes (a source answering more than it should is a failed read).
    public static async Task<byte[]> Read(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new ResponseTooLargeException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximum) throw new ResponseTooLargeException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    sealed class Upstream(Uri upstream, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var original = request.RequestUri!;
            request.RequestUri = new Uri(upstream, original.Host + original.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }
}
