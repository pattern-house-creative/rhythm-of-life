using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Knowledge.Relationships;

// Plain text and Drive links from one downloaded or exported file. Everything stays in memory: callers
// keep only the links (with locators) and a derived similarity vector, never the text. Bounded against
// zip bombs and oversized streams; malformed input yields whatever was safely read.
public static partial class RelationshipContent
{
    public const int MaximumDownload = 25 * 1024 * 1024;
    const int MaximumText = 2 * 1024 * 1024, MaximumPart = 20 * 1024 * 1024, MaximumExpanded = 100 * 1024 * 1024;
    public enum Format { OfficeOpenXml, Pdf, Text, LegacyOffice }

    public sealed record Extracted(string Text, IReadOnlyList<Reference> Links);

    public static Extracted Extract(byte[] body, Format format, int maxLinks)
    {
        var text = new StringBuilder();
        var urls = new List<string>();
        try
        {
            switch (format)
            {
                case Format.OfficeOpenXml: OfficeOpenXml(body, text, urls); break;
                case Format.Pdf: Pdf(body, text, urls); break;
                case Format.Text: Append(text, Encoding.UTF8.GetString(body)); break;
                case Format.LegacyOffice:
                    // Binary Office: links are stored as Latin-1 or UTF-16 strings; the prose is not reliably recoverable.
                    urls.AddRange(UrlPattern().Matches(Encoding.Latin1.GetString(body)).Select(m => m.Value));
                    urls.AddRange(UrlPattern().Matches(Encoding.Unicode.GetString(body)).Select(m => m.Value));
                    break;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or ArgumentException) { }
        var combined = string.Join('\n', urls) + "\n" + text;
        return new(text.ToString(), RelationshipDiscovery.ExtractDriveLinks(combined, maxLinks));
    }

    static void Append(StringBuilder text, string value)
    {
        if (text.Length >= MaximumText) return;
        text.Append(value.AsSpan(0, Math.Min(value.Length, MaximumText - text.Length))).Append('\n');
    }

    // docx/xlsx/pptx and Google Docs/Sheets/Slides exports: external hyperlink relationships plus the
    // visible text of every part (which also carries HYPERLINK formulas and field codes).
    static void OfficeOpenXml(byte[] body, StringBuilder text, List<string> urls)
    {
        using var zip = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Too many parts");
        long expanded = 0;
        static int Priority(string name) => name.Contains("document.xml") || name.Contains("sharedStrings") || name.Contains("/slides/slide") ? 0 : 1;
        foreach (var entry in zip.Entries.OrderBy(e => Priority(e.FullName)).ThenBy(e => e.FullName, StringComparer.Ordinal))
        {
            var rels = entry.FullName.EndsWith(".rels", StringComparison.Ordinal);
            if (!rels && !entry.FullName.EndsWith(".xml", StringComparison.Ordinal)) continue;
            using var stream = entry.Open();
            var bytes = Bounded(stream, MaximumPart);
            if (bytes is null || (expanded += bytes.Length) > MaximumExpanded) break;
            var xml = Encoding.UTF8.GetString(bytes);
            if (rels)
            {
                foreach (var rel in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Relationship"))
                    if ((string?)rel.Attribute("TargetMode") == "External" && (string?)rel.Attribute("Target") is { } target) urls.Add(target);
            }
            else Append(text, WebUtility.HtmlDecode(Tag().Replace(xml, " ")));
        }
    }

    // PDF: link annotations and URLs in the file and in its Flate-compressed streams, plus literal
    // strings shown by text operators. Best effort: fonts with custom encodings yield little text.
    static void Pdf(byte[] body, StringBuilder text, List<string> urls)
    {
        var parts = new List<string> { Encoding.Latin1.GetString(body) };
        long expanded = 0;
        foreach (Match m in StreamStart().Matches(parts[0]))
        {
            var start = m.Index + m.Length;
            var end = parts[0].IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            try
            {
                using var inflate = new ZLibStream(new MemoryStream(body, start, end - start), CompressionMode.Decompress);
                var bytes = Bounded(inflate, MaximumPart);
                if (bytes is null || (expanded += bytes.Length) > MaximumExpanded) break;
                parts.Add(Encoding.Latin1.GetString(bytes));
            }
            catch (InvalidDataException) { }
        }
        foreach (var part in parts)
        {
            urls.AddRange(UriAction().Matches(part).Select(x => x.Groups[1].Value.Replace("\\)", ")").Replace("\\(", "(")));
            urls.AddRange(UrlPattern().Matches(part).Select(x => x.Value));
            Append(text, string.Join(' ', ShownText().Matches(part).Select(x => x.Groups[1].Value)));
        }
    }

    static byte[]? Bounded(Stream stream, int maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximum) return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
    [GeneratedRegex("https://[^\\s<>\"'()\\\\\\x00-\\x1f]+")] private static partial Regex UrlPattern();
    [GeneratedRegex("/URI\\s*\\(((?:\\\\.|[^\\\\)])*)\\)")] private static partial Regex UriAction();
    [GeneratedRegex("(?<!end)stream\\r?\\n")] private static partial Regex StreamStart();
    [GeneratedRegex("\\(((?:\\\\.|[^\\\\)]){1,500})\\)\\s*T[jJ']")] private static partial Regex ShownText();
}
