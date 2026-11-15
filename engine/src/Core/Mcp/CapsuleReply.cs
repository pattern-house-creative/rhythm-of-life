using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowledge.Mcp;

// What an AI client is told about a prompt capsule (SB-P3-08; Core/Capsules.cs), by the get_prompt_capsule tool and the
// capsule resources, both only with memory.capsules on (MemorySettings.CapsulesOn): upstream's MCP shape
// (prompt-capsule-mcp.v1) with the capsule and its strong ETag; with if_none_match naming that ETag, only the ETag and
// unchanged, so a client that holds the capsule reuses it for the cost of one read (upstream's 304, API-44). A refusal is
// {ok: false, schema, code, status, error} and, for an unknown project, the scope's known ones. The capsule's text is
// the scope's own memories, framed as data: a client builds its prompt from it, it is never the engine's instruction.
public static class CapsuleReply
{
    // Whether capsules are served for a call: the settings the services hold, else the loaded ones, else none (off).
    public static bool On(IServiceProvider? services) =>
        (services?.GetService<RhythmConfig>() ?? (RhythmConfig.Loaded ? RhythmConfig.Current : null)) is { } config && MemorySettings.CapsulesOn(config);

    public static async Task<CallToolResult> Tool(PromptCapsules capsules, Guid principal, CapsuleRequest request, string? ifNoneMatch,
        CancellationToken ct)
    {
        try { return new CallToolResult { Content = [new TextContentBlock { Text = await Envelope(capsules, principal, request, ifNoneMatch, ct) }] }; }
        catch (MemoryRefused refused)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Refusal(refused) }] };
        }
    }

    public static async Task<string> Envelope(PromptCapsules capsules, Guid principal, CapsuleRequest request, string? ifNoneMatch, CancellationToken ct)
    {
        var built = await capsules.Build(principal, request, ct);
        var answer = new JsonObject { ["ok"] = true, ["schema"] = CapsuleSlots.McpSchema, ["etag"] = built.Etag };
        if (Capsule.Matches(ifNoneMatch, built.Etag)) answer["unchanged"] = true;
        else answer["capsule"] = built.Payload.DeepClone();
        return CapsuleJson.Write(answer);
    }

    public static string Refusal(MemoryRefused refused)
    {
        var (code, status) = refused.Code switch
        {
            "forbidden" => ("forbidden", 403),
            "unknown_project" => ("unknown_project", 404),
            "too_many_candidates" => ("too_many_candidates", 409),
            _ => ("invalid_request", 400),
        };
        var body = new JsonObject { ["ok"] = false, ["schema"] = CapsuleSlots.McpSchema, ["code"] = code, ["status"] = status, ["error"] = refused.Message };
        if (refused.Known is { } known) body["known_projects"] = new JsonArray([.. known.Select(k => (JsonNode)JsonValue.Create(k))]);
        return CapsuleJson.Write(body);
    }
}

// The capsule resources: rhythm://capsules/core (the caller's own scope's core capsule), rhythm://capsules/{scope}/core
// and rhythm://capsules/{scope}/projects/{project} (scope private or shared, or upstream's personal and company), each
// read as the caller, its text the tool's answer. None while memory.capsules is off, so resources/list is as before.
public static class CapsuleResources
{
    public const string CoreUri = "rhythm://capsules/core";
    public const string ScopedCoreUri = "rhythm://capsules/{scope}/core";
    public const string ProjectUri = "rhythm://capsules/{scope}/projects/{project}";
    const string Mime = "application/json";

    public static IReadOnlyList<McpServerResource> Create(RhythmConfig config) => MemorySettings.CapsulesOn(config)
        ?
        [
            McpServerResource.Create((IServiceProvider services, CancellationToken ct) => Read(services, new(CapsuleSlots.Core), ct),
                new McpServerResourceCreateOptions { UriTemplate = CoreUri, Name = "capsule-core", MimeType = Mime,
                    Description = "The core prompt capsule of your own scope: your identity, preferences, constraints and principles, one canonical memory each, with its ETag." }),
            McpServerResource.Create((IServiceProvider services, string scope, CancellationToken ct) => Read(services, new(CapsuleSlots.Core, null, scope), ct),
                new McpServerResourceCreateOptions { UriTemplate = ScopedCoreUri, Name = "capsule-core-scope", MimeType = Mime,
                    Description = "The core prompt capsule of one scope (private or shared), with its ETag." }),
            McpServerResource.Create((IServiceProvider services, string scope, string project, CancellationToken ct) =>
                    Read(services, new(CapsuleSlots.Project, project, scope), ct),
                new McpServerResourceCreateOptions { UriTemplate = ProjectUri, Name = "capsule-project", MimeType = Mime,
                    Description = "A project's prompt capsule in one scope (private or shared): its current state, decisions and open questions, with its ETag." }),
        ]
        : [];

    static async Task<string> Read(IServiceProvider services, CapsuleRequest request, CancellationToken ct)
    {
        if (!CapsuleReply.On(services))
            throw new McpException("Prompt capsules are off in this install (memory.capsules)");
        var principal = services.GetService<IHttpContextAccessor>()?.HttpContext?.Items["principal"] is Guid id ? id : throw new McpException("Unavailable");
        try { return await CapsuleReply.Envelope(new PromptCapsules(services.GetRequiredService<Database>().Source), principal, request, null, ct); }
        catch (MemoryRefused refused) { throw new McpException(CapsuleReply.Refusal(refused)); }
    }
}
