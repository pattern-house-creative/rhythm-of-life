using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge;

// The original file behind a document star someone added (an upload or a file of the documents folder,
// docs/adapters/documents.md): what the reader needs to show it, in app_graph's content answer, and the file itself,
// which the app reads from GET /app/documents/<id>. Only rows of provider `documents` that name an archived file
// have one; a save_document copy is text only.
public static class DocumentOriginals
{
    public const string Provider = "documents";
    public static readonly string[] Formats = ["pdf", "text", "markdown"];

    // The columns Describe reads, after a resource row aliased `r`.
    public const string Columns = "r.provider,r.raw_path IS NOT NULL,r.metadata->>'format',r.metadata->>'bytes',r.metadata->>'fileName'";
    public static GraphOriginal? Describe(Guid id, string provider, bool archived, string? format, string? bytes, string? name) =>
        provider == Provider && archived && Formats.Contains(format) && long.TryParse(bytes, CultureInfo.InvariantCulture, out var size) && size > 0
            ? new(id.ToString(), format!, size, FileName(name, format!)) : null;

    // A name that is safe in a download header and keeps its type: no folders, quotes or control characters, at most
    // 200 characters, ending in the format's extension.
    public static string FileName(string? name, string format)
    {
        var extension = format switch { "pdf" => ".pdf", "markdown" => ".md", _ => ".txt" };
        var clean = new string(Path.GetFileName((name ?? "").Replace('\\', '/')).Select(c => char.IsControl(c) || "\"/\\:*?<>|".Contains(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        if (clean.Length == 0) clean = "document" + extension;
        if (!string.Equals(Path.GetExtension(clean), extension, StringComparison.OrdinalIgnoreCase)
            && !(format == "markdown" && string.Equals(Path.GetExtension(clean), ".markdown", StringComparison.OrdinalIgnoreCase)))
            clean += extension;
        return clean.Length <= 200 ? clean : clean[..(196 - extension.Length)] + "…" + extension;
    }

    public static string ContentType(string format) => format switch
    {
        "pdf" => "application/pdf", "markdown" => "text/markdown; charset=utf-8", _ => "text/plain; charset=utf-8"
    };
}

// What a harness hands save_document: a document it has read in full, with the source's own id for it. A Subject left
// out (null) is the caller's own person with memory.scopes on, when the caller has one, else the household's
// (migration 031).
public record DocumentSnapshot(string FileId, string Title, string? Url, string Content, DateTimeOffset ModifiedAt,
    DateTimeOffset? CheckedAt = null, string Domain = "household", bool SemanticIndex = true, string? Subject = null);

// save_document: a searchable, dated copy of one document, kept as provider "documents" and optionally embedded
// locally for semantic_search. The original stays unchanged wherever it lives. Generalized from the private
// version's household-only save, which accepted only one document store's URLs. With memory.scopes on the database
// lands it only in the caller's own person's scope or, for a caller that reads Family, the household's, and refuses
// any other subject (42501, Elsewhere) before it reads anything there (docs/dev/memory.md section 5, migration 031).
public sealed class DocumentStore(NpgsqlDataSource writer, Embeddings embeddings)
{
    // The words migration 031 refuses another subject with.
    public const string Elsewhere = "A document is saved only for your own person or, as a Family member, the household";

    public async Task<string> Save(Guid principal, DocumentSnapshot s, CancellationToken ct)
    {
        if (s.Content is null || s.Content.Length > 40000 || string.IsNullOrWhiteSpace(s.Content)
           || string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 300 || s.FileId is null
           || !Regex.IsMatch(s.FileId, "\\A[A-Za-z0-9_.:-]{1,200}\\z")
           || s.Domain is null || !Regex.IsMatch(s.Domain, "\\A[a-z][a-z0-9-]{0,63}\\z")
           || s.Url is not null && (s.Url.Length > 2000 || !Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "")
           || s.ModifiedAt > DateTimeOffset.UtcNow.AddMinutes(5)
           || s.CheckedAt is { } checkedAt && (checkedAt < DateTimeOffset.UtcNow.AddMinutes(-15) || checkedAt > DateTimeOffset.UtcNow.AddMinutes(1))
           || s.Subject is not null && !RhythmConfig.Current.IsSubject(s.Subject))
            throw new ArgumentException("Use a complete document of at most 40000 characters, with its source id, an https URL (or none), its modified time and a subject from this install.");
        var chunks = new List<object>();
        var model = s.SemanticIndex ? embeddings.Model : null;
        if (model is not null)
            foreach (var text in model.Chunks(s.Content))
            {
                ct.ThrowIfCancellationRequested();
                chunks.Add(new { text, model = model.Id, vector = "[" + string.Join(',', model.Embed(text).Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]" });
            }
        // A document asked to be indexed while the model is off is saved without passages. It is marked for the index
        // only when the backfill (adapters.embeddings.backfill, off by default) will give it them later; otherwise its
        // row says it is not indexed, as before, so what an install stores and serves is unchanged unless it asks.
        await using var command = writer.CreateCommand("SELECT save_document(@principal,@subject,@file,@title,@url,@content,@modified,@domain,@semantic,@chunks::jsonb)");
        foreach (var (key, value) in new (string, object?)[]{("principal",principal),("subject",s.Subject),("file",s.FileId),("title",s.Title),("url",s.Url),("content",s.Content),
            ("modified",s.ModifiedAt.ToUniversalTime()),("domain",s.Domain),("semantic",s.SemanticIndex && (model is not null || embeddings.Backfill)),("chunks",JsonSerializer.Serialize(chunks))})
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        command.Parameters["url"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        command.Parameters["subject"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        var id = (Guid)(await command.ExecuteScalarAsync(ct))!;
        return JsonSerializer.Serialize(new { saved = true, resourceId = id, sourceUrl = s.Url, sourceModifiedAt = s.ModifiedAt,
            revision = Hashing.Hash(s.Content), characters = s.Content.Length, semanticChunks = chunks.Count,
            semanticIndex = model is not null ? "indexed" : s.SemanticIndex ? "embeddings-off" : "not-requested",
            refreshMode = "on-request", retrievalMode = "dated-snapshot", originalChanged = false });
    }
}
