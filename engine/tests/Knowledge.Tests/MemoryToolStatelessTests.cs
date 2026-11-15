using Knowledge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowledge.Tests;

// The client's name the memory tools record (SB-P3-09, MemoryTools.ClientName; ledger MCP-23), through the transport the
// engine serves /mcp with: stateless HTTP (Program.cs, WithHttpTransport(o => o.Stateless = true), MapMcp("/mcp")). The
// other memory tool tests run the tools over an in-process stream, which keeps the handshake's clientInfo for the whole
// connection; a stateless server keeps nothing between requests, so it knows a client's name only when each request
// carries it (protocol 2026-07-28 on). A client on an older protocol gives it once, at initialize, and its changes are
// recorded with no client. Fictional client names only.
public sealed class MemoryToolStatelessTests
{
    [Fact] public async Task Through_the_stateless_transport_a_client_is_named_only_when_its_requests_name_it()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var probe = McpServerTool.Create((IServiceProvider services) => MemoryTools.ClientName(services) ?? "(none)",
            new McpServerToolCreateOptions { Name = "client_name" });
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools([probe]);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        var http = app.GetTestClient();

        async Task<(string Version, string Name)> Named(string? version)
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false),
                new McpClientOptions { ClientInfo = new() { Name = "DEMO client", Version = "1.0.0" }, ProtocolVersion = version });
            var r = await client.CallToolAsync("client_name", new Dictionary<string, object?>());
            return (client.NegotiatedProtocolVersion!, string.Join(" ", r.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }

        // The SDK's own default, the newest protocol: each request names its client.
        var newest = await Named(null);
        Assert.Equal("DEMO client", newest.Name);
        Assert.True(string.CompareOrdinal(newest.Version, "2026-07-28") >= 0, newest.Version);
        // Older protocols name the client only at initialize: no name on the call.
        Assert.Equal(("2025-11-25", "(none)"), await Named("2025-11-25"));
        Assert.Equal(("2025-06-18", "(none)"), await Named("2025-06-18"));
        await app.StopAsync();
    }
}
