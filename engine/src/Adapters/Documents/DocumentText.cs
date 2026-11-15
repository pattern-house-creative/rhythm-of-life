using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Knowledge.Adapters.Documents;

// The readable text of one PDF, text or Markdown file, page by page. PDF text comes from PdfPig (Apache-2.0); a PDF
// that is only scanned images has no text, and Rhythm does no OCR. What the text says is data, never instructions.
public static partial class DocumentText
{
    public const int MaxPages = 2000, MaxPageText = 40_000;
    public sealed record Read(string Format, string? Title, IReadOnlyList<string> Pages);

    public static string Format(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "pdf", ".txt" => "text", ".md" or ".markdown" => "markdown", _ => ""
    };

    public static Read Extract(string name, byte[] bytes)
    {
        var format = Format(name);
        if (format == "pdf") return Pdf(bytes);
        if (format == "") throw new DocumentRefused("unsupported_type", "Only PDF, .txt and .md files can be added.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿').Replace("\r\n", "\n"); }
        catch (DecoderFallbackException) { throw new DocumentRefused("not_text", "This file is not UTF-8 text."); }
        if (text.Contains('\0')) throw new DocumentRefused("not_text", "This file is not text.");
        return new(format, format == "markdown" ? MarkdownTitle(text) : null, [text]);
    }

    static Read Pdf(byte[] bytes)
    {
        if (bytes.Length < 8 || !Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("%PDF-"))
            throw new DocumentRefused("not_pdf", "This file is not a PDF.");
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > MaxPages) throw new DocumentRefused("too_many_pages", $"This PDF has more than {MaxPages} pages.");
            var pages = new List<string>(pdf.NumberOfPages);
            foreach (var page in pdf.GetPages())
            {
                var text = Blank().Replace(ContentOrderTextExtractor.GetText(page).Replace("\r\n", "\n"), "\n\n").Trim();
                pages.Add(text.Length <= MaxPageText ? text : text[..MaxPageText]);
            }
            // A PDF's own title field is often a tool's leftover ("Microsoft Word - draft1"), so the file name is used.
            return new("pdf", null, pages);
        }
        catch (DocumentRefused) { throw; }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // Encrypted, damaged or unusual PDFs: said once, never with the library's message (it may quote the file).
            throw new DocumentRefused("pdf_unreadable", "This PDF could not be read; it may be encrypted or damaged.");
        }
    }

    // A front-matter title, else the first "# " heading.
    static string? MarkdownTitle(string text)
    {
        var front = FrontTitle().Match(text);
        if (front.Success) return Clean(front.Groups[1].Value);
        var heading = Heading().Match(text);
        return heading.Success ? Clean(heading.Groups[1].Value) : null;
        static string? Clean(string t) => t.Trim().Trim('"', '\'').Trim() is { Length: > 0 and <= 200 } c ? c : null;
    }

    [GeneratedRegex(@"\n{3,}")] private static partial Regex Blank();
    [GeneratedRegex(@"\A---\n(?:.*\n)*?title:[ \t]*(.+)\n(?:.*\n)*?---", RegexOptions.IgnoreCase)] private static partial Regex FrontTitle();
    [GeneratedRegex(@"^#[ \t]+(.+)$", RegexOptions.Multiline)] private static partial Regex Heading();
}
