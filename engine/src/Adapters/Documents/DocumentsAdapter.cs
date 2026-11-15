using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Adapters.Documents;

// Documents people bring (docs/adapters/documents.md): PDFs, text and Markdown files dropped in the documents folder
// (adapters.documents.path, /data/import/documents) or uploaded in the app (app_upload_document). Each one is archived
// as it came, its text read, and saved under the core's `documents` provider (archive, like save_document), so it
// becomes a document star that opens in the reader and is found by search_knowledge and get_resource. Long documents
// are kept as pages under one record. The folder is the truth for its own files: a changed file updates, a removed one
// leaves the map. Uploads stay until they are removed by other means.
public sealed class DocumentsAdapter : IImportSource, IDocumentUpload
{
    public const string AdapterName = "documents", Provider = "documents";
    const long MaxFolderFile = 20 * 1024 * 1024;
    // Longer text is kept as pages (one per PDF page, or sections), so no one record outgrows the search index.
    const int MaxRecordText = 40_000, OverviewText = 2000;
    public string Name => AdapterName;
    // It writes the core's own `documents` provider (ProviderPolicies.Core: archive, household), so it claims none.
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public long MaxUploadBytes => 5 * 1024 * 1024;
    public IReadOnlyList<string> UploadTypes { get; } = [".pdf", ".txt", ".md", ".markdown"];
    SourceFolder folder = new("/data/import/documents", "adapters.documents.path");
    string owner = RhythmConfig.Household;
    // Whose documents the folder holds (adapters.documents.owner): Sync now shows it to their readers (IAdapter.Owner).
    public string Owner => owner;
    AdapterContext? context;
    readonly Dictionary<string, (long Length, DateTimeOffset Modified)> seen = new(StringComparer.Ordinal);
    // Files of the folder left out, and why (an encrypted PDF, a file too large), for get_sync_status.
    readonly Dictionary<string, string> refused = new(StringComparer.Ordinal);

    public void Configure(AdapterContext context)
    {
        this.context = context;
        folder = new(context.Settings.String("path") ?? "/data/import/documents", "adapters.documents.path");
        owner = context.Settings.Owner ?? RhythmConfig.Household;
    }

    // The stored scope of a subject's documents: the household's are shared, a person's private.
    static string ScopeOf(string subject) => subject == RhythmConfig.Household ? "shared" : "private";
    bool Wanted(string path) => UploadTypes.Contains(Path.GetExtension(path).ToLowerInvariant());
    // The star a saved document stands for (the catalog's own: CatalogStars.Records).
    public string GraphId(string externalId) => GraphId(owner, externalId);
    static string GraphId(string subject, string externalId) => new ResourceKey(subject, CatalogStars.Provider, Provider, $"{subject}:{externalId}").GraphId;

    // ---- the folder --------------------------------------------------------------------------------------------------
    public async Task Import(CancellationToken ct)
    {
        if (context is null || !folder.Exists) return;
        var files = folder.Files(Wanted);
        var changed = false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.TryGetValue(file.Path, out var last) && last == (file.Length, file.Modified)) continue;
            refused.Remove(file.Path);
            if (file.Length is 0 or > MaxFolderFile) refused[file.Path] = file.Length == 0 ? "empty" : "too_large";
            else
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file.Full, ct);
                    changed |= (await Save("file:" + file.Path, Path.GetFileName(file.Path), bytes, file.Modified, "folder", null, ct)).Status != "unchanged";
                }
                // Still being copied, or not readable now: tried again next time.
                catch (IOException) { continue; }
                // Not a document Rhythm can read (an encrypted PDF): left out until it changes.
                catch (DocumentRefused e) { refused[file.Path] = e.Code; }
            seen[file.Path] = (file.Length, file.Modified);
        }
        var present = files.Select(f => "file:" + f.Path).ToArray();
        foreach (var gone in seen.Keys.Where(k => !present.Contains("file:" + k)).ToArray()) { seen.Remove(gone); refused.Remove(gone); }
        await Status(files.Count, ct);
        // Files no longer in the folder leave the map, with their pages; the archived originals stay.
        await using (var cmd = context.Db.Source.CreateCommand("""
            WITH gone AS (SELECT external_id FROM resources WHERE subject_id=@s AND provider=@p AND status='current'
              AND left(external_id,5)='file:' AND position('#part:' IN external_id)=0 AND NOT (external_id=ANY(@present)))
            UPDATE resources r SET status='deleted',content='',indexed_at=now() FROM gone
            WHERE r.subject_id=@s AND r.provider=@p AND (r.external_id=gone.external_id OR left(r.external_id,length(gone.external_id)+6)=gone.external_id || '#part:')
            """))
        {
            cmd.Parameters.AddWithValue("s", owner); cmd.Parameters.AddWithValue("p", Provider);
            cmd.Parameters.AddWithValue("present", present);
            changed |= await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        if (changed) await new RefreshRequests(context.Db.Source).Queue(ct);
    }

    // The folder's line in get_sync_status: how many files it holds and which were left out, and why.
    async Task Status(int files, CancellationToken ct)
    {
        var left = refused.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        var today = context!.Config.Today();
        var metadata = new JsonObject
        {
            ["folder"] = "documents", ["files"] = files, ["leftOut"] = left.Length,
            ["refused"] = new JsonArray(left.Take(20).Select(r => (JsonNode)new JsonObject { ["file"] = r.Key, ["code"] = r.Value }).ToArray()),
        };
        await using var cmd = context.Db.Source.CreateCommand("""
            INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,status,attempts,last_error,updated_at,metadata)
            VALUES(@id,@s,@p,@today,@today,@today,@status,0,@error,now(),@metadata::jsonb)
            ON CONFLICT(id) DO UPDATE SET subject_id=excluded.subject_id,from_date=excluded.from_date,until_date=excluded.until_date,
              next_date=excluded.next_date,status=excluded.status,last_error=excluded.last_error,metadata=excluded.metadata,
              updated_at=CASE WHEN sync_jobs.metadata=excluded.metadata AND sync_jobs.status=excluded.status THEN sync_jobs.updated_at ELSE now() END
            """);
        cmd.Parameters.AddWithValue("id", Guid.ParseExact(Hashing.Hash("documents-folder:" + folder.Root)[..32], "N"));
        cmd.Parameters.AddWithValue("s", owner); cmd.Parameters.AddWithValue("p", Provider); cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("status", left.Length == 0 ? "complete" : "partial");
        cmd.Parameters.AddWithValue("error", left.Length == 0 ? DBNull.Value
            : $"{left.Length} of {files} files left out: " + string.Join("; ", left.Take(20).Select(r => $"{r.Key} {r.Value}")));
        cmd.Parameters.AddWithValue("metadata", metadata.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- an upload from the app --------------------------------------------------------------------------------------
    // It lands in the owner's scope, as the folder's files do, unless a landing names another subject (a viewer
    // session's person, or Family; IDocumentUpload). Each subject keeps its own: the same file added by Alex for herself
    // and to Family is two documents.
    public async Task<JsonObject> Upload(Guid principal, string name, byte[] content, CancellationToken ct, UploadLanding? landing = null)
    {
        if (context is null) throw new InvalidOperationException("The documents adapter is not configured");
        name = name.Trim();
        if (name.Length is 0 or > 200 || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl) || name.StartsWith('.'))
            throw new DocumentRefused("invalid_name", "Give the file a plain name, up to 200 characters.");
        if (!Wanted(name)) throw new DocumentRefused("unsupported_type", "Only PDF, .txt and .md files can be added.");
        if (content.Length == 0) throw new DocumentRefused("empty", "This file is empty.");
        if (content.Length > MaxUploadBytes) throw new DocumentRefused("too_large", $"Files up to {MaxUploadBytes / 1024 / 1024} MiB can be added.");
        // The same file uploaded again is the same document; only its name may change. A viewer session's own upload is
        // keyed apart from the ones the owner setting puts in the same person's scope (UploadLanding.Own).
        var subject = landing?.Subject ?? owner;
        var key = (landing is { Own: true } ? "upload:own:" : "upload:") + Hashing.Hash(content)[..32];
        var saved = await Save(key, name, content, DateTimeOffset.UtcNow, "upload", principal, ct, subject);
        if (saved.Status != "unchanged") await new RefreshRequests(context.Db.Source).Queue(ct);
        var document = new JsonObject
        {
            ["graphId"] = GraphId(subject, saved.ExternalId), ["resourceId"] = saved.Id.ToString(), ["title"] = saved.Title,
            ["format"] = saved.Format, ["pages"] = saved.Pages, ["characters"] = saved.Characters, ["status"] = saved.Status,
        };
        // Who reads it: with scopes off everyone in the household, wherever it is kept (6.1); with them on, one person's
        // readers (a session's own person, or the person the owner setting names) or Family's.
        if (landing is not null) document["scope"] = landing.ScopesOn ? ScopeOf(subject) : "shared";
        return new JsonObject { ["document"] = document };
    }

    // ---- saving one document -----------------------------------------------------------------------------------------
    public sealed record Saved(Guid Id, string ExternalId, string Title, string Format, int Pages, int Characters, string Status);

    // Saved under `subject`, or the owner when it is not given (the folder's files always are).
    internal async Task<Saved> Save(string externalId, string name, byte[] bytes, DateTimeOffset modified, string origin, Guid? principal, CancellationToken ct,
        string? subject = null)
    {
        var db = context!.Db;
        var landed = subject ?? owner;
        var checksum = Hashing.Hash(bytes);
        var fallback = Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : name;
        await using var c = await db.Source.OpenConnectionAsync(ct);
        // Unchanged (same bytes, same name, still current): nothing to do, not even reading the text again.
        await using (var cmd = Database.Command(c, null, """
            SELECT id,title,metadata->>'format',coalesce((metadata->>'pages')::int,0),length(content),(metadata->>'fileName')=@name
            FROM resources WHERE subject_id=@s AND provider=@p AND external_id=@e AND status='current' AND checksum=@checksum
            """, ("s", landed), ("p", Provider), ("e", externalId), ("checksum", checksum), ("name", name)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct) && reader.GetBoolean(5))
                return new(reader.GetGuid(0), externalId, reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), "unchanged");
        var read = DocumentText.Extract(name, bytes);
        var title = read.Format == "pdf" ? fallback : read.Title ?? fallback;
        title = title.Length <= 200 ? title : title[..199] + "…";
        var text = string.Join("\n\n", read.Pages.Where(p => p.Length > 0));
        var archived = await context.Archive.Save(bytes, ct);
        var metadata = new JsonObject
        {
            ["fileName"] = name, ["format"] = read.Format, ["bytes"] = bytes.Length, ["pages"] = read.Format == "pdf" ? read.Pages.Count : 1,
            ["origin"] = origin, ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["retrievalMode"] = "archived-original",
        };
        if (principal is { } who) metadata["savedBy"] = who.ToString();
        if (text.Length == 0) metadata["noText"] = true;
        var parts = text.Length <= MaxRecordText ? [] : Parts(read).ToArray();
        // A document whose text is too long for one record keeps a short overview and its pages; one with no text
        // layer (a scan) says so, so it still has a star that opens.
        var body = text.Length == 0 ? "This document has no text that Rhythm can read (it may be a scan). The original file is archived."
            : parts.Length == 0 ? text : text[..OverviewText];
        await using var tx = await c.BeginTransactionAsync(ct);
        string status;
        await using (var prior = Database.Command(c, tx, "SELECT status FROM resources WHERE subject_id=@s AND provider=@p AND external_id=@e FOR UPDATE",
            ("s", landed), ("p", Provider), ("e", externalId)))
            status = await prior.ExecuteScalarAsync(ct) is "current" ? "updated" : "created";
        var id = await db.Upsert(new(landed, Provider, externalId, "document", ScopeOf(landed), title, null, body, checksum, modified,
            RawPath: archived.Path, Checksum: archived.Checksum, Metadata: metadata.ToJsonString()), [], c, tx, ct);
        var kept = new List<Guid>();
        foreach (var (part, i) in parts.Select((p, i) => (p, i + 1)))
        {
            var label = read.Format == "pdf" ? $"page {part.Page}" : $"part {i}";
            var partId = await db.Upsert(new(landed, Provider, $"{externalId}#part:{i}", read.Format == "pdf" ? "page" : "section", ScopeOf(landed),
                $"{title} · {label}", null, part.Text, Hashing.Hash(part.Text), modified, Checksum: Hashing.Hash(part.Text),
                Metadata: new JsonObject { ["part"] = i, ["of"] = externalId }.ToJsonString()), [], c, tx, ct);
            await using (var link = Database.Command(c, tx, """
                INSERT INTO relationships(source_id,target_id,type,verification,evidence) VALUES(@parent,@part,'contains','verified','documents:pages')
                ON CONFLICT(source_id,target_id,type) DO NOTHING
                """, ("parent", id), ("part", partId)))
                await link.ExecuteNonQueryAsync(ct);
            await db.AddDependency(partId, id, c, tx, ct);
            kept.Add(partId);
        }
        // Pages a longer earlier version had are gone now.
        await using (var stale = Database.Command(c, tx, """
            UPDATE resources SET status='deleted',content='',indexed_at=now() WHERE subject_id=@s AND provider=@p AND status='current'
              AND left(external_id,length(@prefix))=@prefix AND id<>ALL(@kept)
            """, ("s", landed), ("p", Provider), ("prefix", externalId + "#part:"), ("kept", kept.ToArray())))
            await stale.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(id, externalId, title, read.Format, read.Format == "pdf" ? read.Pages.Count : 1, text.Length, status);
    }

    // A PDF's pages as they are; a long text in sections of at most MaxRecordText characters, cut at a paragraph.
    static IEnumerable<(int Page, string Text)> Parts(DocumentText.Read read)
    {
        if (read.Format == "pdf")
        {
            foreach (var (page, i) in read.Pages.Select((p, i) => (p, i + 1)))
                if (page.Length > 0) yield return (i, page);
            yield break;
        }
        var text = read.Pages[0];
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + MaxRecordText);
            if (end < text.Length && text.LastIndexOf("\n\n", end - 1, end - start, StringComparison.Ordinal) is var cut and > 0 && cut > start + MaxRecordText / 2)
                end = cut + 2;
            yield return (0, text[start..end]);
            start = end;
        }
    }
}
