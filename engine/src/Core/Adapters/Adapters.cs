using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using ModelContextProtocol.Server;

namespace Knowledge.Adapters;

// The seams every integration plugs into (docs/dev/adapters.md). An adapter is one class in
// engine/src/Adapters/<Name>/ that implements IAdapter plus one or more of the kinds below, and is listed in
// AdapterCatalog. It is configured from `adapters.<name>` in rhythm.yaml, reads its secrets from files named by
// RHYTHM_<NAME>_<PURPOSE>_FILE, and does nothing unless `enabled: true`.
public interface IAdapter
{
    // Matches the rhythm.yaml section `adapters.<name>`: ^[a-z][a-z0-9-]{1,30}$.
    string Name { get; }
    // The provider ids this adapter writes, each with its policy. Fixed in code, never taken from a caller.
    IReadOnlyDictionary<string, ProviderPolicy> Providers { get; }
    // Called once at start when the adapter is enabled. Throw ConfigException for a bad setting.
    void Configure(AdapterContext context) { }
    // Harness tools that exist only while the adapter is on (contracts/README.md 2.4).
    IEnumerable<McpServerTool> HarnessTools() => [];
    // App tools that exist only while the adapter is on (contracts/README.md 2.2), such as a vendor's tiles.
    IEnumerable<AppTool> AppTools() => [];
    // The subject this adapter's rows land in by default (docs/dev/memory.md 10.2), when it states one. With
    // memory.scopes on, the app's Sync now shows an adapter, and runs one, only for a caller that reads that subject's
    // rows of every provider it writes (SyncRequests); one that states none (null) is shown to no one then, so an
    // adapter that forgets to say whose rows it writes fails closed. Every adapter on the engine's clock states one
    // (pinned by SyncRequestsTests).
    string? Owner => null;
}

// One app-role tool an adapter adds: its tools/list entry, a strict check of its arguments (anything outside its
// schema is refused before any work), and the call, which returns the structuredContent. Aliases are older names
// accepted on tools/call and listed only with engine.legacyToolNames. The app endpoint does the transport and the
// errors: a PostgresException 42501 answers 403 and 22023 answers 400.
public sealed record AppTool(string Name, string Description, object InputSchema, bool ReadOnly, Func<JsonElement, bool> Allowed,
    Func<Guid, JsonElement, CancellationToken, Task<JsonObject>> Call, string Summary, IReadOnlyList<string>? Aliases = null)
{
    public object Listing => new { name = Name, description = Description, inputSchema = InputSchema,
        annotations = new { readOnlyHint = ReadOnly, destructiveHint = false, openWorldHint = false } };
}

// What an enabled adapter gets: its settings, the install's settings, the database and the archive.
public sealed record AdapterContext(AdapterSettings Settings, RhythmConfig Config, Database Db, Archive Archive);

// MeasurementSource: brings in numeric samples. The scheduler calls Sync every Interval (and once at start);
// the adapter writes through the sink, which applies the same rules as the ingest_measurements tool.
public interface IMeasurementSource : IAdapter
{
    TimeSpan Interval { get; }
    Task Sync(MeasurementSink sink, CancellationToken ct);
}

// What a graph source returns for one refresh pass. Complete means the listing is the whole of what the source
// holds, so records it listed last time and not now are reported missing and leave the map. Note, when a source has one, is
// what the pass could not read or left out, for the engine's log with the map's own notes: kinds and counts only, never an
// id, a title, an address or a source's own words.
public sealed record SourceListing(IReadOnlyList<DiscoveryRecord> Records, bool Complete)
{
    public string? Note { get; init; }
}

// What a graph source may use during a pass: the records it listed last time (to reuse work for unchanged ones),
// the local similarity vector (null when embeddings are off) and the size caps from `graph`. Model is the model that
// makes this pass's vectors (null when embeddings are off); the pass tags every vector with its model itself.
public sealed record SourcePass(IReadOnlyDictionary<ResourceKey, DiscoveryRecord> Previous,
    Func<ResourceKey, string, string, string, float[]?> Vector, int MaxMemories, int MaxDocuments, string? Model = null)
{
    // With memory.scopes on, what is left of each subject's own share of the caps (RelationshipCatalog.Scoped), so one
    // scope's records never use another's: a source lists at most MemoriesOf(subject) memories and DocumentsOf(subject)
    // documents of each subject it lists, and MaxMemories and MaxDocuments are then the largest of them. Null with scopes
    // off, when both caps are the install's and every subject's records count against them together.
    public IReadOnlyDictionary<string, int>? MemoryShares { get; init; }
    public IReadOnlyDictionary<string, int>? DocumentShares { get; init; }
    public int MemoriesOf(string subject) => MemoryShares is null ? MaxMemories : MemoryShares.GetValueOrDefault(subject);
    public int DocumentsOf(string subject) => DocumentShares is null ? MaxDocuments : DocumentShares.GetValueOrDefault(subject);
    // Whether the pass runs with memory.scopes on (it then has shares). A source whose revisions fold in its records'
    // links names only those to records of the record's own subject then: a Family record's revision never moves with a
    // person's private record it links to (docs/dev/memory.md section 8).
    public bool Scoped => MemoryShares is not null || DocumentShares is not null;

    // Whether a record from the last pass holds a vector another model made, so its text must be read again for a new
    // one even though it has not changed. Never while embeddings are off: a kept vector then stays as it is. With a map
    // model (MapModel, graph.similarity.model, D-256), also a vector without that model's map vector beside it, so a map
    // model turned on or changed makes each one once.
    public bool Outdated(DiscoveryRecord previous) => Model is not null && previous.Embedding is not null
        && (SimilaritySuggestions.ModelOf(previous) != Model || MapModel is not null && previous.MapEmbeddingModel != MapModel);
    // The map model that makes this pass's map vectors beside Model's (the pass tags them itself); null without one.
    public string? MapModel { get; init; }

    // A record's whole text for the titles model (D-255): what its star shows when it is opened, given by the source for a
    // record it lists, so only for one its own rules let be a star. The pass keeps it in memory, beside the record and
    // never on it, for a record of the source's own providers whose title is still its excerpt, and only while a titles
    // model is on (TitleTexts is null otherwise); it sends it to that model in place of the excerpt and drops it once the
    // pass has titled. Never stored, logged or put on a record.
    public void TitleText(DiscoveryRecord record, string text) => TitleTexts?.Invoke(record, text);
    public Action<DiscoveryRecord, string>? TitleTexts { get; init; }
}

// MemorySource: memories or notes, with links and projects, as stars on the map. Text is read when a star opens.
public interface IMemorySource : IAdapter
{
    Task<SourceListing> List(SourcePass pass, CancellationToken ct);
    Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct);
}

// DocumentSource: documents with revisions, as stars on the map; their contents feed dashboards.
public interface IDocumentSource : IAdapter
{
    Task<SourceListing> List(SourcePass pass, CancellationToken ct);
    // A dashboard contents feed for one of this adapter's stars: format (text, markdown, csv or sheet), title and
    // text or sheets, as docs/dev/adapters.md describes; null when the record is not this adapter's.
    Task<JsonObject?> Contents(DiscoveryRecord record, CancellationToken ct);
    // Whether a stored copy of a live document is still current at the source (retrieval checks live providers).
    Task<bool> Current(string provider, string externalId, string? revision, CancellationToken ct) => Task.FromResult(false);
}

// SearchableSource: a graph source whose records' text the embeddings index may read, to give the record passages for
// semantic_search (SB-P2-05), when adapters.embeddings.sources names the adapter. The text is read on request, in the
// index job's own budget and never during a map pass, and returned with the revision of the source it was read at:
// passages are kept only when that is the revision the record's star row holds. Null when the record has no text to
// read (gone, not this adapter's, not readable as words); throw for a failure worth trying again (the source down).
// Only a provider that may keep a source's text gets passages: never a reference-only one (ProviderPolicies.Validate).
// Its text is data: an adapter never logs it and nothing follows it as instructions.
public interface ISearchableSource : IAdapter
{
    Task<SourceText?> SearchText(DiscoveryRecord record, CancellationToken ct);
}
public sealed record SourceText(string Text, string Revision);

// CitedSource: a live source whose documents a native memory may cite (SB-P4-10, story 7; docs/dev/memory.md 10.4). Check
// describes one document as its source holds it now, read at the call: its revision, when it was last changed, its name
// and its folders, or why it can't be read. A metadata read only: no content is downloaded, nothing is kept, and no map
// pass, sync or stored row is touched, so a citation's check never changes what the source's sync does.
public interface ICitedSource : IAdapter
{
    Task<CitedCheck> Check(string provider, string externalId, CancellationToken ct);
}

// What a check found. State: ok (Revision, ModifiedAt, Title and Parents as the source holds them now), trashed (in the
// source's trash), gone (the source has no such document, or no longer shares it with Rhythm), unreadable (the source
// refuses Rhythm it, or Rhythm's sign-in to it no longer works) or unchecked (the source did not answer: nothing is known
// about now). Reason says why in plain words for every state but ok. One more is the engine's own, never an adapter's:
// unavailable, the map's word that the source's sync no longer finds the document where Rhythm is given to read (its
// newest row on the map missing), so it is not looked up at the source at all (CitationChecks).
public sealed record CitedCheck(string State, string? Revision = null, DateTimeOffset? ModifiedAt = null, string? Title = null,
    IReadOnlyList<string>? Parents = null, string? Reason = null)
{
    public const string Ok = "ok", Trashed = "trashed", Gone = "gone", Unreadable = "unreadable", Unchecked = "unchecked",
        Unavailable = "unavailable";
    public static CitedCheck Not(string state, string reason) => new(state, Reason: reason);
}

// Writer: performs a dashboard's declared writes of the kinds it names (for example sheet and doc) at the source.
// The core has already checked the declaration and that edits are allowed; the writer checks the revision, makes
// the change and returns what it replaced, so Undo can put it back.
public interface IDashboardWriter : IAdapter
{
    IReadOnlyList<string> WriteKinds { get; }
    Task<WriteOutcome> Write(WriteRequest request, CancellationToken ct);
    Task Undo(UndoRequest request, CancellationToken ct);
}
public sealed record WriteRequest(DashboardWrite Declared, DiscoveryRecord Star, JsonElement Change);
public sealed record WriteOutcome(JsonObject Target, string Summary, JsonNode? Before, JsonNode After);
public sealed record UndoRequest(string Kind, JsonObject Target, JsonNode? Before, JsonNode After);

// ImportSource: files an adapter copies into the catalog on its own clock (a drop folder). The scheduler calls Import
// at start and every Interval; what it saved shows on the next map refresh as the catalog's own stars, so the adapter
// needs no listing of its own.
public interface IImportSource : IAdapter
{
    TimeSpan Interval { get; }
    Task Import(CancellationToken ct);
}

// Sync now from the app's Sources view (SB-P6-09, SyncRequests). Any MeasurementSource or ImportSource can be asked for
// one batch now: by default one Sync or Import call, the same the scheduler makes, at most once per its Interval (and
// never more often than once a minute). An adapter whose regular call alone would not reach its source when asked, or
// whose source limits how often it may be asked, implements this: one asked-for batch is then SyncNow, made by the
// scheduler between its own jobs for the principal that asked, at most once per SyncLimit (never under a minute).
// SyncNow answers whether the batch reached its source: false when the source's own rule turned it away (the batch then
// ends failed, not complete, and the engine's log says why).
public interface ISyncNow : IAdapter
{
    TimeSpan SyncLimit { get; }
    Task<bool> SyncNow(Guid principal, MeasurementSink sink, CancellationToken ct);
}

// A document a person hands the app (app_upload_document): checked, archived, its text read, and saved as a
// document star. The app tool exists only while an adapter implementing this is on.
public interface IDocumentUpload : IAdapter
{
    // The largest file accepted, in bytes, and the file name extensions accepted (lower case, with the dot).
    long MaxUploadBytes { get; }
    IReadOnlyList<string> UploadTypes { get; }
    // {document: {graphId, resourceId, title, format, pages, characters, status}}; throws DocumentRefused for a file
    // that is not accepted, with a reason fit to show. Without a landing the document lands where the adapter's
    // settings put it, as always. With one (only while app.viewers is on, AppEndpoint; docs/dev/memory.md section 5)
    // it lands in the landing's subject, or where the settings put it when that is null, and the answer's document also
    // says who reads it (scope: private, one person's, or shared, everyone's in the household).
    Task<JsonObject> Upload(Guid principal, string name, byte[] content, CancellationToken ct, UploadLanding? landing = null);
}
// Where an upload from the app lands (ViewerSessions.Landing): a subject (the household's, or a viewer session's
// person), or null for the adapter's own setting; whether memory.scopes is on, which decides who reads it; and whether it
// is the caller's own private scope (a viewer session's person's, with scopes on). An upload of one's own is kept under
// keys of its own, so it never meets an upload the adapter's owner setting put in the same person's scope from a login
// that does not read it (the household view), which would tell that login whether the file is there.
public sealed record UploadLanding(string? Subject, bool ScopesOn = false, bool Own = false);
public sealed class DocumentRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// TitleModel: short titles for stars that have none. Off falls back to excerpts.
public interface ITitleModel : IAdapter
{
    // A label for the model and the text it would be sent to title, so a stored title is reused while both are unchanged.
    string Revision(string text);
    Task<string?> Title(string text, CancellationToken ct);
}

// Optional, for an adapter whose `rhythm connect <name>` needs more than the prompts in its descriptor
// (contracts/adapters/<name>.json), such as a browser consent. The CLI writes the descriptor's settings and secrets
// first, then calls Connect. Write new secret files only under SecretsDir, as <name>_<purpose>, mode 0600.
public interface IAdapterConnect : IAdapter
{
    Task<int> Connect(ConnectContext context, CancellationToken ct);
}
public sealed record ConnectContext(AdapterSettings Settings, RhythmConfig Config, string SecretsDir,
    TextReader Input, TextWriter Output, IReadOnlyList<string> Args);

// Writes measurements for a MeasurementSource with the ingest rules: one source resource per
// (provider, subject, externalId), idempotent samples, provenance and, under an archive policy, the original kept.
public sealed class MeasurementSink(Ingest ingest, IAdapter adapter)
{
    // details: the adapter's own description of the source (for example an activity's type), kept with the source
    // resource as metadata.details; at most 4 KiB and never a secret.
    public Task<JsonObject> Write(JsonElement arguments, CancellationToken ct, JsonObject? details = null) =>
        ingest.Measurements(adapter.Name, arguments, ct, adapter.Providers.Keys.ToArray(), details);
    // Text records (settings, events, notes) beside the numbers, with the ingest_records rules.
    public Task<JsonObject> Records(JsonElement arguments, CancellationToken ct) =>
        ingest.Records(adapter.Name, arguments, ct, adapter.Providers.Keys.ToArray());
}

// Every adapter this build knows, enabled or not. A new adapter adds one line here.
public static class AdapterCatalog
{
    public static IReadOnlyList<Func<IAdapter>> Known { get; } =
    [
        () => new Demo.DemoAdapter(),
        () => new Notes.NotesAdapter(),
        () => new Csv.CsvAdapter(),
        () => new Documents.DocumentsAdapter(),
        () => new Nightscout.NightscoutAdapter(),
        () => new Garmin.GarminAdapter(),
        () => new Titles.TitlesAdapter(),
        () => new Drive.DriveAdapter(),
        () => new SecondBrain.SecondBrainAdapter(),
        () => new Calendar.CalendarAdapter(),
    ];
}

// The adapters that are on for this install, by kind. Built once at start.
public sealed partial class AdapterRegistry
{
    public IReadOnlyList<IAdapter> Enabled { get; }
    public IReadOnlyList<IMeasurementSource> Measurements => Enabled.OfType<IMeasurementSource>().ToArray();
    public IReadOnlyList<IMemorySource> Memories => Enabled.OfType<IMemorySource>().ToArray();
    public IReadOnlyList<IDocumentSource> Documents => Enabled.OfType<IDocumentSource>().ToArray();
    public IReadOnlyList<IDashboardWriter> Writers => Enabled.OfType<IDashboardWriter>().ToArray();
    public IReadOnlyList<IImportSource> Imports => Enabled.OfType<IImportSource>().ToArray();
    public ITitleModel? Titles => Enabled.OfType<ITitleModel>().FirstOrDefault();
    public IDocumentUpload? Uploads => Enabled.OfType<IDocumentUpload>().FirstOrDefault();
    // The enabled adapters' app tools by name and by alias. A name that clashes with a core tool or another adapter's
    // stops the start.
    public IReadOnlyDictionary<string, AppTool> AppTools => appTools.Value;
    readonly Lazy<IReadOnlyDictionary<string, AppTool>> appTools;
    readonly HashSet<string> shown;

    public AdapterRegistry(IEnumerable<IAdapter> enabled)
    {
        Enabled = enabled.ToArray();
        shown = [.. GraphProviders];
        appTools = new(() =>
        {
            var tools = new Dictionary<string, AppTool>(StringComparer.Ordinal);
            foreach (var tool in Enabled.SelectMany(a => a.AppTools()))
                foreach (var name in (tool.Aliases ?? []).Prepend(tool.Name))
                    if (!AppToolPattern().IsMatch(name) || Mcp.AppEndpoint.IsCore(name) || !tools.TryAdd(name, tool))
                        throw new ConfigException($"app tool {name} is not valid or is already taken");
            return tools;
        });
    }
    public static AdapterRegistry Empty { get; } = new([]);

    // The enabled adapters, not yet configured. The demo adapter follows `demo.enabled` rather than an adapters section.
    // Their providers are fixed in code, so the install's one Database, with every adapter's provider policies
    // (Policies), is built from this registry before Configure hands that Database to each adapter.
    public static AdapterRegistry For(RhythmConfig config, IEnumerable<Func<IAdapter>>? known = null)
    {
        var enabled = new List<IAdapter>();
        foreach (var make in known ?? AdapterCatalog.Known)
        {
            var adapter = make();
            if (!RhythmConfig.AdapterPattern().IsMatch(adapter.Name)) throw new ConfigException($"adapter name {adapter.Name} is not valid");
            if (adapter.Name == Demo.DemoAdapter.AdapterName ? config.Demo.Enabled : config.AdapterEnabled(adapter.Name)) enabled.Add(adapter);
        }
        if (enabled.SelectMany(a => a.Providers.Keys).GroupBy(p => p).Any(g => g.Count() > 1))
            throw new ConfigException("two adapters claim the same provider id");
        return new AdapterRegistry(enabled);
    }

    // Each enabled adapter, configured once at start with the install's Database, built with Policies(config).
    public AdapterRegistry Configure(RhythmConfig config, Database db, Archive archive)
    {
        foreach (var adapter in Enabled) adapter.Configure(new(config.Adapter(adapter.Name), config, db, archive));
        _ = AppTools;
        return this;
    }

    // Provider policies: the core's, every enabled adapter's, then each enabled plugin's, per provider (contracts/README.md 3.2).
    public ProviderPolicies Policies(RhythmConfig config)
    {
        var extra = new Dictionary<string, ProviderPolicy>(StringComparer.Ordinal);
        foreach (var adapter in Enabled)
            foreach (var (provider, policy) in adapter.Providers) extra[provider] = policy;
        foreach (var (_, plugin) in config.Plugins.Where(p => p.Value.Enabled))
            foreach (var provider in plugin.Providers)
                extra.TryAdd(provider, new(plugin.PolicyFor(provider) switch
                {
                    "archive" => SourceAuthority.AuthorizedArchive,
                    "reference" => SourceAuthority.ReferenceOnly,
                    _ => SourceAuthority.LiveSource
                }, plugin.Domain));
        // Providers no adapter or plugin owns, kept from an older engine or a removed plugin; the core's own stay as they are.
        foreach (var (provider, kept) in config.Providers.Where(p => !ProviderPolicies.Core.ContainsKey(p.Key)))
            extra.TryAdd(provider, new(kept.Policy switch
            {
                "archive" => SourceAuthority.AuthorizedArchive,
                "reference" => SourceAuthority.ReferenceOnly,
                _ => SourceAuthority.LiveSource
            }, kept.Domain));
        return new(extra);
    }

    // The provider ids of every enabled graph source (Memory- or DocumentSource).
    public IEnumerable<string> GraphProviders => Enabled.Where(a => a is IMemorySource or IDocumentSource).SelectMany(a => a.Providers.Keys);
    // Whether the map shows a provider's records: the catalog's own stars, the native memories' (MemoryStars, Rhythm's
    // own records, which no adapter turns off) and every enabled graph source's. A provider whose adapter is off is not
    // shown, though its records stay in the snapshot until a rebuild retires them.
    public bool Shows(string provider) => provider == CatalogStars.Provider || provider == MemoryStars.Provider || shown.Contains(provider);

    [GeneratedRegex("^(app|observatory)_[a-z0-9_]{1,40}$")] private static partial Regex AppToolPattern();
}
