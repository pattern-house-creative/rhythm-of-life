using Knowledge.Relationships;
using Microsoft.Net.Http.Headers;

namespace Knowledge.Mcp;

// GET /app/documents/<id> for the app role only (contracts/README.md 1.5): one document's original file as the archive
// keeps it, streamed, so the reader can show a PDF's pages rather than its text. <id> is the resource id app_graph's
// content answer names in resource.original. Only a current `documents` record of a subject in this install that
// names an archived file, and that the caller reads through the one read predicate (Database.Visible,
// docs/dev/memory.md section 7.4), is served; any other id answers 404, as an unknown one does. The file is found by
// the path and checksum the catalog recorded, never by anything in the request, and checked against its checksum
// first. Nothing about the file is logged.
public static class AppFiles
{
    public const string Prefix = "/app/documents/";

    // The one shape this route takes: the prefix and a resource id, nothing after it.
    public static bool Matches(PathString path, out Guid id)
    {
        id = Guid.Empty;
        var value = path.Value ?? "";
        return value.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(value[Prefix.Length..], "D", out id);
    }

    public static async Task Handle(HttpContext context, Guid principal, Guid id, Database db, Archive archive, RhythmConfig config)
    {
        if (context.Request.Method != "GET") { context.Response.Headers.Allow = "GET"; context.Response.StatusCode = 405; return; }
        try { await Serve(context, principal, id, db, archive, config); }
        // The database or the archive is not answering: said once, as for the app's other calls.
        catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            RequestLog.Of(context)?.Failed("app", "document", 503, "unavailable", e);
            if (!context.Response.HasStarted) await JsonRpc.Refuse(context, 503, null, "unavailable", JsonRpc.Message(503));
            else context.Abort();
        }
    }

    static async Task Serve(HttpContext context, Guid principal, Guid id, Database db, Archive archive, RhythmConfig config)
    {
        var ct = context.RequestAborted;
        string rawPath, checksum; GraphOriginal? original;
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT r.raw_path,r.checksum,{DocumentOriginals.Columns} FROM resources r
            WHERE r.id=@id AND r.provider=@p AND r.kind='document' AND {Database.Visible()} AND r.relationship_snapshot IS NULL
              AND r.subject_id=ANY(@subjects) AND r.checksum IS NOT NULL
            """))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("principal", principal);
            cmd.Parameters.AddWithValue("p", DocumentOriginals.Provider);
            cmd.Parameters.AddWithValue("subjects", RelationshipCatalog.For(config).Subjects);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) { await NotFound(context); return; }
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            (rawPath, checksum) = (reader.GetString(0), reader.GetString(1));
            original = DocumentOriginals.Describe(id, reader.GetString(2), reader.GetBoolean(3), Text(4), Text(5), Text(6));
        }
        if (original is null) { await NotFound(context); return; }
        // A file that no longer matches its checksum is never served (Archive.Open throws, answered 503 above).
        var file = await archive.Open(rawPath, checksum, ct);
        if (file is null) { await NotFound(context); return; }
        await using (file)
        {
            var response = context.Response;
            response.StatusCode = 200;
            response.ContentType = DocumentOriginals.ContentType(original.format);
            response.ContentLength = file.Length;
            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(original.name);
            response.Headers.ContentDisposition = disposition.ToString();
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            await file.CopyToAsync(response.Body, ct);
        }
    }

    static Task NotFound(HttpContext context) => JsonRpc.Refuse(context, 404, null, "not_found", "That document has no archived file.");
}
