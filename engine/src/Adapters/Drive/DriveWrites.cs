using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Relationships;

namespace Knowledge.Adapters.Drive;

// The drive adapter as a Writer: a dashboard's declared sheet and doc writes, made with the edit grant after checking that
// the file hasn't changed since the dashboard read it (its Drive version, which the contents feed gave the page as
// `revision`). Each returns what it replaced, so Undo can put it back while nobody has changed it since. The core has
// already checked the declaration and that someone allowed edits for the dashboard.
public sealed partial class DriveAdapter
{
    public const int MaxWriteCells = 1000, MaxCellText = 50_000, MaxDocText = 20_000;
    public IReadOnlyList<string> WriteKinds => edits ? ["sheet", "doc"] : [];

    public async Task<WriteOutcome> Write(WriteRequest request, CancellationToken ct)
    {
        if (request.Star.Key.Provider != Provider) throw new DashboardWriteRefused("That star isn't a Google Drive file.");
        var change = request.Change;
        try
        {
            var file = await GoogleFile.Open(Google, request.Star.Key.ExternalId, request.Declared.Kind, ct);
            file.Current(DashboardWriter.Text(change, "revision", 100) ?? throw new DashboardWriteRefused("revision is the file's revision from its feed."));
            var (target, summary, before, after) = request.Declared.Kind == "sheet"
                ? await SheetWrite(file, request.Declared, change, ct) : await DocWrite(file, request.Declared, change, ct);
            target["file"] = file.Id; target["title"] = file.Title;
            return new(target, summary, before, after);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new DashboardWriteRefused("Google couldn't be reached to make that change; nothing was written."); }
    }

    public async Task Undo(UndoRequest request, CancellationToken ct)
    {
        try
        {
            var file = await GoogleFile.Open(Google, (string?)request.Target["file"] ?? "", request.Kind, ct);
            if (request.Kind == "sheet") await SheetUndo(file, request.Target, request.Before, request.After, ct);
            else await DocUndo(file, request.Target, request.Before, request.After, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new DashboardWriteRefused("Google couldn't be reached to undo that change; it is still there."); }
    }

    // Sheet: {revision, cells: "B4" or "B4:C6" within the declared range, values: [[...]]} or {revision, row: [...]}.
    static async Task<(JsonObject, string, JsonNode?, JsonNode)> SheetWrite(GoogleFile google, DashboardWrite declared, JsonElement change, CancellationToken ct)
    {
        if (change.TryGetProperty("row", out var row))
        {
            DashboardWriter.Only(change, "revision", "row");
            if (declared.AppendTo is null) throw new DashboardWriteRefused("This write sets cells; it can't add rows.");
            var cells = Values(row, 1, 100)[0];
            var tab = $"'{declared.AppendTo.Replace("'", "''")}'";
            var appended = await google.Send(HttpMethod.Post, $"{GoogleApi.SheetsUrl}/{google.Id}/values/{Uri.EscapeDataString(tab + "!A1")}:append"
                + "?valueInputOption=RAW&insertDataOption=INSERT_ROWS&includeValuesInResponse=false",
                new JsonObject { ["majorDimension"] = "ROWS", ["values"] = new JsonArray(cells) }, ct);
            var range = (string?)appended["updates"]?["updatedRange"] ?? throw new DashboardWriteRefused("Google didn't say where the row went.");
            return (new JsonObject { ["tab"] = declared.AppendTo, ["appended"] = range },
                $"Added a row to {google.Title} › {declared.AppendTo}", null, new JsonObject { ["range"] = range, ["values"] = cells.DeepClone() });
        }
        DashboardWriter.Only(change, "revision", "cells", "values");
        if (declared.Range is null || A1Range.Parse(declared.Range) is not { } allowed) throw new DashboardWriteRefused("This write adds rows; it can't set cells.");
        var at = DashboardWriter.Text(change, "cells", 30) is { } cellsText ? A1Range.Parse($"'{allowed.Tab.Replace("'", "''")}'!{cellsText}") : null;
        if (at is null || at.LastColumn == int.MaxValue || at.LastRow == int.MaxValue)
            throw new DashboardWriteRefused("cells names cells on the declared tab, such as B4 or B4:C6.");
        if (!allowed.Contains(at)) throw new DashboardWriteRefused($"{at.Cells} is outside the cells this dashboard may set ({declared.Range}).");
        if (!change.TryGetProperty("values", out var given)) throw new DashboardWriteRefused("values holds the rows of new values.");
        var values = Values(given, at.Rows, at.Columns);
        if (values.Length != at.Rows || values.Any(r => r.Count != at.Columns))
            throw new DashboardWriteRefused($"values must be {at.Rows} row(s) of {at.Columns} value(s) for {at.Cells}.");
        // Every cell of the range, so Undo also empties the cells that were empty.
        var read = await google.Cells(at, ct);
        var before = new JsonArray(Enumerable.Range(0, at.Rows).Select(i => (JsonNode)new JsonArray(Enumerable.Range(0, at.Columns).Select(j =>
            i < read.Count && read[i] is JsonArray r && j < r.Count ? r[j]?.DeepClone() : JsonValue.Create("")).ToArray())).ToArray());
        await google.Send(HttpMethod.Put, $"{GoogleApi.SheetsUrl}/{google.Id}/values/{Uri.EscapeDataString(at.ToString())}?valueInputOption=RAW",
            new JsonObject { ["range"] = at.ToString(), ["majorDimension"] = "ROWS", ["values"] = new JsonArray(values.Select(r => (JsonNode)r).ToArray()) }, ct);
        return (new JsonObject { ["tab"] = at.Tab, ["range"] = at.ToString() }, $"Set {at.Cells} in {google.Title} › {at.Tab}", before,
            new JsonArray(values.Select(r => (JsonNode)r.DeepClone()).ToArray()));
    }

    static async Task SheetUndo(GoogleFile google, JsonObject target, JsonNode? before, JsonNode after, CancellationToken ct)
    {
        if (target["appended"] is JsonValue appendedRange)
        {
            var at = A1Range.Parse((string)appendedRange!) ?? throw new DashboardWriteRefused("That row can't be found.");
            var now = await google.Cells(at, ct);
            if (!Same(now, new JsonArray(after["values"]!.DeepClone()))) throw new DashboardWriteRefused($"That row in {google.Title} has changed since, so it was left as it is.");
            var sheetId = await google.SheetId(at.Tab, ct);
            await google.Send(HttpMethod.Post, $"{GoogleApi.SheetsUrl}/{google.Id}:batchUpdate", new JsonObject {
                ["requests"] = new JsonArray(new JsonObject { ["deleteDimension"] = new JsonObject { ["range"] = new JsonObject {
                    ["sheetId"] = sheetId, ["dimension"] = "ROWS", ["startIndex"] = at.FirstRow - 1, ["endIndex"] = at.LastRow } } }) }, ct);
            return;
        }
        var range = A1Range.Parse((string?)target["range"]) ?? throw new DashboardWriteRefused("Those cells can't be found.");
        var current = await google.Cells(range, ct);
        if (!Same(current, after)) throw new DashboardWriteRefused($"Those cells in {google.Title} have changed since, so they were left as they are.");
        // As the cells held them: formulas as formulas, numbers as numbers, text kept as text.
        var restore = new JsonArray(((JsonArray)before!).Select(r => (JsonNode)new JsonArray(((JsonArray)r!).Select(v =>
            v is JsonValue s && s.TryGetValue<string>(out var text) && !text.StartsWith('=') && text.Length > 0 ? JsonValue.Create("'" + text) : v?.DeepClone()).ToArray())).ToArray());
        await google.Send(HttpMethod.Put, $"{GoogleApi.SheetsUrl}/{google.Id}/values/{Uri.EscapeDataString(range.ToString())}?valueInputOption=USER_ENTERED",
            new JsonObject { ["range"] = range.ToString(), ["majorDimension"] = "ROWS", ["values"] = restore }, ct);
    }

    // Rows of strings, numbers and booleans, at most `rows` by `columns` and MaxWriteCells in all.
    static JsonArray[] Values(JsonElement given, int rows, int columns)
    {
        var grid = given.ValueKind == JsonValueKind.Array && given.EnumerateArray().All(r => r.ValueKind == JsonValueKind.Array)
            ? given.EnumerateArray().ToArray() : given.ValueKind == JsonValueKind.Array ? [given] : [];
        if (grid.Length is 0 || grid.Length > rows || grid.Sum(r => r.GetArrayLength()) > MaxWriteCells
            || grid.Any(r => r.GetArrayLength() is 0 || r.GetArrayLength() > columns))
            throw new DashboardWriteRefused($"values holds up to {rows} row(s) of up to {columns} value(s), {MaxWriteCells} in all.");
        return grid.Select(r => new JsonArray(r.EnumerateArray().Select(v => v.ValueKind switch
        {
            JsonValueKind.String when v.GetString()!.Length <= MaxCellText => JsonValue.Create(v.GetString()),
            JsonValueKind.Number when v.TryGetDouble(out var n) && double.IsFinite(n) => JsonValue.Create(n),
            JsonValueKind.True or JsonValueKind.False => (JsonNode?)JsonValue.Create(v.GetBoolean()),
            _ => throw new DashboardWriteRefused("A cell's value is text, a number or true/false.")
        }).ToArray())).ToArray();
    }

    // Doc: {revision, text} for a section or the end; {revision, row, column, text} for a table cell (0-based).
    static async Task<(JsonObject, string, JsonNode?, JsonNode)> DocWrite(GoogleFile google, DashboardWrite declared, JsonElement change, CancellationToken ct)
    {
        var doc = await google.Document(ct);
        var text = DashboardWriter.Text(change, "text", MaxDocText) ?? throw new DashboardWriteRefused("text is the new text.");
        text = text.Replace("\r\n", "\n");
        if (declared.Append == true)
        {
            DashboardWriter.Only(change, "revision", "text");
            if (text.Trim().Length == 0) throw new DashboardWriteRefused("text is the text to add.");
            var added = "\n" + text.TrimEnd('\n');
            await google.Batch(doc.RevisionId, [new JsonObject { ["insertText"] = new JsonObject { ["endOfSegmentLocation"] = new JsonObject(), ["text"] = added } }], ct);
            return (new JsonObject { ["append"] = true }, $"Added to the end of {google.Title}", null, JsonValue.Create(added)!);
        }
        if (declared.Section is { } heading)
        {
            DashboardWriter.Only(change, "revision", "text");
            var (start, end, old, last) = doc.Section(heading) ?? throw new DashboardWriteRefused($"{google.Title} has no heading \"{heading}\" now.");
            // The Doc's own last newline ends the last section; any other section ends with one of its own.
            var replaced = last ? text.TrimEnd('\n') : text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
            // A last heading with nothing under it: a new paragraph after the heading, not text joined to it.
            var requests = last && start == end && replaced.Length > 0
                ? [new JsonObject { ["insertText"] = new JsonObject { ["location"] = new JsonObject { ["index"] = start }, ["text"] = "\n" + replaced } },
                   Style(start + 1, start + 1 + replaced.Length)]
                : Replace(start, end, replaced, true);
            await google.Batch(doc.RevisionId, requests, ct);
            return (new JsonObject { ["section"] = heading }, $"Rewrote \"{heading}\" in {google.Title}", JsonValue.Create(old), JsonValue.Create(replaced)!);
        }
        DashboardWriter.Only(change, "revision", "text", "row", "column");
        int row = Index(change, "row"), column = Index(change, "column");
        if (text.Contains('\n')) throw new DashboardWriteRefused("A table cell's text is one line.");
        var (cellStart, cellEnd, was) = doc.Cell(declared.Table!.Value, row, column)
            ?? throw new DashboardWriteRefused($"{google.Title} has no table {declared.Table + 1} cell at row {row + 1}, column {column + 1}.");
        await google.Batch(doc.RevisionId, Replace(cellStart, cellEnd, text, false), ct);
        return (new JsonObject { ["table"] = declared.Table, ["row"] = row, ["column"] = column },
            $"Set table {declared.Table + 1}, row {row + 1}, column {column + 1} in {google.Title}", JsonValue.Create(was), JsonValue.Create(text)!);
    }

    static async Task DocUndo(GoogleFile google, JsonObject target, JsonNode? before, JsonNode after, CancellationToken ct)
    {
        var doc = await google.Document(ct);
        var wrote = (string)after!;
        if (target["append"] is not null)
        {
            var end = doc.BodyEnd - 1;
            if (!doc.Text.EndsWith(wrote + "\n", StringComparison.Ordinal)) throw new DashboardWriteRefused($"The end of {google.Title} has changed since, so it was left as it is.");
            await google.Batch(doc.RevisionId, [Delete(end - wrote.Length, end)], ct);
            return;
        }
        var (start, stop, now) = target["section"] is JsonValue section ? doc.Section((string)section!) is { } s ? (s.Start, s.End, s.Text) : default
            : doc.Cell((int)target["table"]!, (int)target["row"]!, (int)target["column"]!) ?? default;
        if (now is null || now != wrote) throw new DashboardWriteRefused($"That part of {google.Title} has changed since, so it was left as it is.");
        await google.Batch(doc.RevisionId, Replace(start, stop, (string?)before ?? "", target["section"] is not null), ct);
    }

    // Deletes [start, end) and puts `text` there; under a heading, as body text.
    static JsonObject[] Replace(int start, int end, string text, bool body)
    {
        var requests = new List<JsonObject>();
        if (end > start) requests.Add(Delete(start, end));
        if (text.Length > 0)
        {
            requests.Add(new JsonObject { ["insertText"] = new JsonObject { ["location"] = new JsonObject { ["index"] = start }, ["text"] = text } });
            // Positions count UTF-16 units, as .NET strings do.
            if (body) requests.Add(Style(start, start + text.Length));
        }
        return [.. requests];
    }
    static JsonObject Style(int start, int end) => new() { ["updateParagraphStyle"] = new JsonObject {
        ["range"] = new JsonObject { ["startIndex"] = start, ["endIndex"] = end },
        ["paragraphStyle"] = new JsonObject { ["namedStyleType"] = "NORMAL_TEXT" }, ["fields"] = "namedStyleType" } };
    static JsonObject Delete(int start, int end) => new() { ["deleteContentRange"] = new JsonObject { ["range"] = new JsonObject { ["startIndex"] = start, ["endIndex"] = end } } };

    static int Index(JsonElement change, string name) => change.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) && n is >= 0 and <= 1000 ? n : throw new DashboardWriteRefused($"{name} is a number from 0.");
    // Cells read back from Google against cells written: ragged rows read as empty, numbers by value.
    static bool Same(JsonNode current, JsonNode written)
    {
        var a = (JsonArray)current; var b = (JsonArray)written;
        if (a.Count > b.Count && a.Skip(b.Count).Any(r => r is JsonArray row && row.Any(v => !Empty(v)))) return false;
        for (var i = 0; i < b.Count; i++)
        {
            var x = i < a.Count ? (JsonArray)a[i]! : []; var y = (JsonArray)b[i]!;
            for (var j = 0; j < Math.Max(x.Count, y.Count); j++)
            {
                var u = j < x.Count ? x[j] : null; var v = j < y.Count ? y[j] : null;
                if (Empty(u) && Empty(v)) continue;
                if (u is JsonValue p && v is JsonValue q && p.TryGetValue<double>(out var m) && q.TryGetValue<double>(out var n)) { if (m != n) return false; continue; }
                if (u?.ToJsonString() != v?.ToJsonString()) return false;
            }
        }
        return true;
    }
    static bool Empty(JsonNode? v) => v is null || v is JsonValue s && s.TryGetValue<string>(out var t) && t.Length == 0;

    // One Google Sheet or Doc: its Drive version read with the read grant, changes made with the edit grant.
    sealed class GoogleFile
    {
        readonly GoogleApi google;
        string? version;
        public string Id { get; }
        public string Title { get; private set; } = "the file";
        GoogleFile(GoogleApi google, string id) { this.google = google; Id = id; }

        public static async Task<GoogleFile> Open(GoogleApi google, string id, string kind, CancellationToken ct)
        {
            if (!DriveId().IsMatch(id)) throw new DashboardWriteRefused("That file can't be found.");
            var file = new GoogleFile(google, id);
            var token = await google.AccessToken(GoogleApi.ReadGrant, ct) ?? throw new DashboardWriteRefused(google.Problem(GoogleApi.ReadGrant));
            var (status, body) = await google.Send(HttpMethod.Get, $"{GoogleApi.DriveUrl}/files/{id}?fields=id,name,mimeType,trashed,version&supportsAllDrives=true",
                token, null, 65_536, ct);
            if (status != HttpStatusCode.OK) throw new DashboardWriteRefused("That file isn't in Drive now, or this install's Google access can't see it.");
            var meta = JsonNode.Parse(body)!;
            if ((string?)meta["id"] != id || meta["trashed"] is JsonValue t && t.GetValue<bool>()) throw new DashboardWriteRefused("That file isn't in Drive now.");
            var mime = (string?)meta["mimeType"];
            if (kind == "sheet" && mime != "application/vnd.google-apps.spreadsheet") throw new DashboardWriteRefused("Only a Google Sheet's cells can be set.");
            if (kind == "doc" && mime != "application/vnd.google-apps.document")
                throw new DashboardWriteRefused("Only a native Google Doc can be edited; open the file in Drive and use File › Save as Google Docs first.");
            file.Title = (string?)meta["name"] ?? "the file";
            file.version = (string?)meta["version"];
            return file;
        }
        // Refuses when the file changed after the dashboard read it.
        public void Current(string revision)
        {
            if (version is null || version != revision)
                throw new DashboardWriteRefused($"{Title} changed after the dashboard read it, so nothing was written. It has been read again; try once more.");
        }
        public async Task<JsonNode> Send(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
        {
            var token = await google.AccessToken(GoogleApi.EditGrant, ct) ?? throw new DashboardWriteRefused(google.Problem(GoogleApi.EditGrant));
            var (status, bytes) = await google.Send(method, url, token, body, 8_000_000, ct);
            if ((int)status is < 200 or > 299)
                throw new DashboardWriteRefused(GoogleApi.Disabled(bytes)
                        ? $"The Google {(url.StartsWith(GoogleApi.DocsUrl, StringComparison.Ordinal) ? "Docs" : "Sheets")} API isn't switched on in your Google Cloud project yet."
                    : status == HttpStatusCode.BadRequest && Encoding.UTF8.GetString(bytes).Contains("revision", StringComparison.OrdinalIgnoreCase)
                        ? $"{Title} changed while it was being written, so nothing was written. Try once more."
                    : status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                        ? $"This install's Google access can't change {Title}."
                    : $"Google couldn't change {Title} right now.");
            return bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(bytes)!;
        }
        // A range's cells as entered (formulas as formulas), rows as Google returns them.
        public async Task<JsonArray> Cells(A1Range range, CancellationToken ct)
        {
            var read = await Send(HttpMethod.Get, $"{GoogleApi.SheetsUrl}/{Id}/values/{Uri.EscapeDataString(range.ToString())}"
                + "?majorDimension=ROWS&valueRenderOption=FORMULA", null, ct);
            return read["values"] is JsonArray rows ? (JsonArray)rows.DeepClone() : [];
        }
        public async Task<int> SheetId(string tab, CancellationToken ct)
        {
            var sheets = await Send(HttpMethod.Get, $"{GoogleApi.SheetsUrl}/{Id}?fields=sheets.properties(sheetId,title)", null, ct);
            return (sheets["sheets"] as JsonArray ?? []).Select(s => s?["properties"]).FirstOrDefault(p => (string?)p?["title"] == tab)?["sheetId"]?.GetValue<int>()
                ?? throw new DashboardWriteRefused($"{Title} has no tab {tab} now.");
        }
        public async Task<GoogleDoc> Document(CancellationToken ct) =>
            new(await Send(HttpMethod.Get, $"{GoogleApi.DocsUrl}/{Id}?fields=revisionId,body.content", null, ct));
        public Task Batch(string revision, JsonObject[] requests, CancellationToken ct) =>
            Send(HttpMethod.Post, $"{GoogleApi.DocsUrl}/{Id}:batchUpdate", new JsonObject {
                ["requests"] = new JsonArray(requests.Select(r => (JsonNode)r).ToArray()),
                ["writeControl"] = new JsonObject { ["requiredRevisionId"] = revision } }, ct);
    }

    // A Doc's body as the Docs API returns it: headings, paragraphs and top-level tables with their UTF-16 positions.
    sealed class GoogleDoc
    {
        public string RevisionId { get; }
        public int BodyEnd { get; }
        public string Text { get; }
        readonly JsonArray content;
        public GoogleDoc(JsonNode doc)
        {
            RevisionId = (string?)doc["revisionId"] ?? throw new DashboardWriteRefused("Google didn't give the Doc's revision.");
            content = doc["body"]?["content"] as JsonArray ?? [];
            BodyEnd = content.Count > 0 ? (int)content[^1]!["endIndex"]! : 1;
            Text = string.Concat(content.Select(e => Paragraph(e)?.Text ?? ""));
        }
        static (string Text, int Level)? Paragraph(JsonNode? element)
        {
            if (element?["paragraph"] is not JsonObject p) return null;
            var text = string.Concat((p["elements"] as JsonArray ?? []).Select(e => (string?)e?["textRun"]?["content"] ?? ""));
            var style = (string?)p["paragraphStyle"]?["namedStyleType"] ?? "NORMAL_TEXT";
            var level = style == "TITLE" ? 0 : style.StartsWith("HEADING_", StringComparison.Ordinal)
                && int.TryParse(style[8..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 99;
            return (text, level);
        }
        // The text under the heading named `heading`, up to the next heading of the same or a higher level.
        public (int Start, int End, string Text, bool Last)? Section(string heading)
        {
            for (var i = 0; i < content.Count; i++)
            {
                if (Paragraph(content[i]) is not { Level: < 99 } h || !string.Equals(h.Text.Trim(), heading.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var start = (int)content[i]!["endIndex"]!; var end = start; var text = new StringBuilder();
                for (var j = i + 1; j < content.Count; j++)
                {
                    if (Paragraph(content[j]) is { } p && p.Level <= h.Level) break;
                    if (content[j]!["paragraph"] is null) throw new DashboardWriteRefused($"\"{heading}\" holds a table or other element; declare a table write for it.");
                    text.Append(Paragraph(content[j])!.Value.Text); end = (int)content[j]!["endIndex"]!;
                }
                // The body's last newline can't be removed.
                var last = end == BodyEnd || start == BodyEnd;
                if (end == BodyEnd && end > start) { end--; text.Length--; }
                return (Math.Min(start, BodyEnd - 1), Math.Min(end, BodyEnd - 1), text.ToString(), last);
            }
            return null;
        }
        // A cell of the `table`th top-level table (0-based), without the cell's own last newline.
        public (int Start, int End, string Text)? Cell(int table, int row, int column)
        {
            var tables = content.Where(e => e?["table"] is not null).ToArray();
            if (table >= tables.Length || tables[table]!["table"]!["tableRows"] is not JsonArray rows || row >= rows.Count) return null;
            if (rows[row]!["tableCells"] is not JsonArray cells || column >= cells.Count || cells[column]!["content"] is not JsonArray inside || inside.Count == 0) return null;
            var start = (int)inside[0]!["startIndex"]!; var end = (int)inside[^1]!["endIndex"]! - 1;
            var text = string.Concat(inside.Select(e => Paragraph(e)?.Text ?? ""));
            return (start, end, text.EndsWith('\n') ? text[..^1] : text);
        }
    }
}
