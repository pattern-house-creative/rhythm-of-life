using System.Text;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using YamlDotNet.Serialization;

namespace Knowledge.Adapters.Notes;

// A Markdown folder as stars (docs/adapters/notes.md), Obsidian-style. One star per .md file; its [[wikilinks]] (with
// aliases and headings) and relative Markdown links to other notes become verified links; its folder and #tags become
// projects. The folder is the truth: every map refresh lists it whole, so a removed file leaves the map, and opening a
// star reads the file again. The note's text is not copied into the database, except as semantic_search passages when
// adapters.embeddings.sources lists notes (SearchText, read by the index job); similar-note links come from the core
// when graph.similarity.threshold is set. Note text is data: it is never logged and never followed as instructions.
public sealed partial class NotesAdapter : IMemorySource, ISearchableSource
{
    public const string AdapterName = "notes", Provider = "notes";
    const long MaxFile = 1024 * 1024;
    const int MaxLinks = 80, MaxGroups = 20, EmbedText = 8000, OverviewText = 300;
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    SourceFolder folder = new("/data/notes", "adapters.notes.path");
    string owner = RhythmConfig.Household;

    public void Configure(AdapterContext context)
    {
        folder = new(context.Settings.String("path") ?? "/data/notes", "adapters.notes.path");
        owner = context.Settings.Owner ?? RhythmConfig.Household;
    }

    ResourceKey Key(string id) => new(owner, Provider, Provider, id);
    string Scope => owner == RhythmConfig.Household ? "shared" : "private";
    static bool IsNote(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    // ---- one note, parsed --------------------------------------------------------------------------------------------
    internal sealed record Link(string Raw, string Target, int Line, bool Wiki);
    internal sealed record Note(string Path, string Title, string Body, string[] Aliases, string[] Tags, IReadOnlyList<Link> Links);

    internal static Note Parse(string path, string text)
    {
        var (front, body) = FrontMatter(text);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var title = Scalar(front, "title") is { Length: > 0 and <= 200 } t ? t : name;
        var aliases = Strings(front, "aliases").Concat(Strings(front, "alias")).Where(a => a.Length <= 200).ToArray();
        var tags = new List<string>(Strings(front, "tags").Concat(Strings(front, "tag")).Select(t => t.TrimStart('#')));
        var links = new List<Link>();
        var fenced = false;
        var lines = body.Split('\n');
        var offset = text.Length - body.Length == 0 ? 0 : text[..(text.Length - body.Length)].Count(c => c == '\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FencePattern().IsMatch(line)) { fenced = !fenced; continue; }
            if (fenced) continue;
            // Inline code is not a link or a tag.
            line = InlineCode().Replace(line, m => new string(' ', m.Length));
            foreach (Match m in WikiLink().Matches(line))
                if (m.Groups["target"].Value.Trim() is { Length: > 0 } target)
                    links.Add(new(m.Value, target, offset + i + 1, true));
            foreach (Match m in MarkdownLink().Matches(line))
            {
                var target = m.Groups["angle"].Success ? m.Groups["angle"].Value : m.Groups["plain"].Value;
                if (target.StartsWith('#') || SchemePattern().IsMatch(target)) continue;
                target = target.Split('#', 2)[0].Split('?', 2)[0];
                try { target = Uri.UnescapeDataString(target); } catch (UriFormatException) { continue; }
                if (target.Length > 0) links.Add(new(m.Value, target, offset + i + 1, false));
            }
            foreach (Match m in TagPattern().Matches(line)) tags.Add(m.Groups[1].Value.TrimEnd('/'));
        }
        var distinctTags = tags.Where(t => t.Length is > 0 and <= 100 && TagName().IsMatch(t))
            .GroupBy(t => t.ToLowerInvariant()).Select(g => g.First()).ToArray();
        return new(path, title, body, aliases, distinctTags, links);
    }

    // YAML front matter between a first line "---" and the next "---" (or "..."); a block that is not valid YAML is
    // left out and its body kept.
    static (Dictionary<object, object?> Front, string Body) FrontMatter(string text)
    {
        var start = text.StartsWith("---\n", StringComparison.Ordinal) ? 4 : text.StartsWith("---\r\n", StringComparison.Ordinal) ? 5 : -1;
        if (start < 0) return ([], text);
        var end = FrontEnd().Match(text, start);
        if (!end.Success) return ([], text);
        var body = text[(end.Index + end.Length)..];
        try
        {
            var parsed = new DeserializerBuilder().Build().Deserialize<object?>(text[start..end.Index]);
            return (parsed as Dictionary<object, object?> ?? [], body);
        }
        catch (YamlDotNet.Core.YamlException) { return ([], body); }
    }
    static string? Scalar(Dictionary<object, object?> front, string key) =>
        front.FirstOrDefault(p => string.Equals(p.Key as string, key, StringComparison.OrdinalIgnoreCase)).Value is string s ? s.Trim() : null;
    // A list, or one string of names separated by commas or spaces.
    static IEnumerable<string> Strings(Dictionary<object, object?> front, string key) =>
        (front.FirstOrDefault(p => string.Equals(p.Key as string, key, StringComparison.OrdinalIgnoreCase)).Value switch
        {
            string s => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
            IEnumerable<object> items => items.OfType<string>(),
            _ => []
        }).Select(s => s.Trim()).Where(s => s.Length > 0).Take(50);

    // ---- the folder, as stars ----------------------------------------------------------------------------------------
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var notes = new List<(Note Note, SourceFolder.Entry File, string Revision)>();
        // A note too large to read whole, or whose path is too long for a star's id, is left out.
        foreach (var file in folder.Files(IsNote).Where(f => f.Length <= MaxFile && Key(f.Path).GraphId.Length <= 900).Take(pass.MemoriesOf(owner)))
        {
            ct.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(file.Full);
            notes.Add((Parse(file.Path, Decode(bytes)), file, "sha256:" + Hashing.Hash(bytes)));
        }
        var now = DateTimeOffset.UtcNow;
        var index = new Index(notes.Select(n => n.Note).ToArray());
        var records = new List<DiscoveryRecord>();
        var projects = new Dictionary<string, (string Title, string Group)>(StringComparer.Ordinal);
        foreach (var (note, file, revision) in notes)
        {
            var key = Key(note.Path);
            var references = new List<Reference>();
            foreach (var link in note.Links)
            {
                if (references.Count >= MaxLinks) break;
                if (index.Resolve(link, note.Path) is not { } target || target == note.Path || references.Any(r => r.Target!.ExternalId == target)) continue;
                references.Add(new(Key(target), null, $"line:{link.Line}", link.Raw.Length <= 200 ? link.Raw : link.Raw[..199] + "…"));
            }
            var groups = new List<RecordGroup>();
            var dir = Folder(note.Path);
            if (dir is not null)
            {
                Project(projects, "folder:" + dir, dir, references, "In folder");
                groups.Add(new("notes:folder:" + dir, dir));
            }
            else groups.Add(new("notes:root", "Notes"));
            foreach (var tag in note.Tags)
            {
                if (groups.Count >= MaxGroups) break;
                Project(projects, "tag:" + tag.ToLowerInvariant(), "#" + tag, references, "Tagged");
                groups.Add(new("notes:tag:" + tag.ToLowerInvariant(), "#" + tag));
            }
            var star = new DiscoveryRecord(key, Scope, "note", note.Title, null, revision, file.Modified > now ? now : file.Modified, now, references,
                ContentRevision: revision, Embedding: pass.Vector(key, revision, note.Title, note.Body.Length <= EmbedText ? note.Body : note.Body[..EmbedText]),
                Groups: groups, Overview: Overview(note.Body));
            // What opening the star reads (ReadText), for the titles model when one is on (D-255); never kept. Only a note
            // whose title is its excerpt is titled, so only its text is read out of the body.
            if (pass.TitleTexts is not null && star.Title == star.Overview) pass.TitleText(star, note.Body.Trim());
            records.Add(star);
        }
        // A folder sits in its parent folder's project, so nested folders keep their shape on the map.
        foreach (var id in projects.Keys.Where(k => k.StartsWith("folder:", StringComparison.Ordinal)).ToArray())
            for (var dir = Folder(id["folder:".Length..]); dir is not null; dir = Folder(dir))
                projects.TryAdd("folder:" + dir, (dir, "notes:folder:" + dir));
        foreach (var (id, (title, group)) in projects.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var parent = id.StartsWith("folder:", StringComparison.Ordinal) && Folder(id["folder:".Length..]) is { } up
                ? new Reference[] { Member("folder:" + up, "In folder") } : [];
            records.Add(new(Key(id), Scope, "project", title, null, "notes-sha256:" + Hashing.Hash(id + "\n" + title), null, now, parent,
                Groups: [new(group, title)]));
        }
        return Task.FromResult(new SourceListing(records, Complete: true));
    }

    Reference Member(string project, string excerpt) =>
        new(Key(project), null, "notes:" + (project.StartsWith("tag:", StringComparison.Ordinal) ? "tag" : "folder"), excerpt, "in_project", true, "verified", 1, true);
    void Project(Dictionary<string, (string, string)> projects, string id, string title, List<Reference> references, string excerpt)
    {
        projects.TryAdd(id, (title, "notes:" + id));
        if (references.Count < 100) references.Add(Member(id, excerpt));
    }
    static string? Folder(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : null;

    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct)
    {
        if (record.Kind != "note" || record.Key.Provider != Provider || !IsNote(record.Key.ExternalId)) return Task.FromResult<string?>(null);
        var file = folder.Resolve(record.Key.ExternalId);
        if (file is null || file.Length > MaxFile) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(FrontMatter(Decode(File.ReadAllBytes(file.FullName))).Body.Trim());
    }

    // The note's text for its passages (the index job, SB-P2-05): what opening its star reads, with the revision List
    // gives the file, so passages are kept only for the version the star stands for.
    public Task<SourceText?> SearchText(DiscoveryRecord record, CancellationToken ct)
    {
        if (record.Kind != "note" || record.Key.Provider != Provider || !IsNote(record.Key.ExternalId)) return Task.FromResult<SourceText?>(null);
        var file = folder.Resolve(record.Key.ExternalId);
        if (file is null || file.Length > MaxFile) return Task.FromResult<SourceText?>(null);
        var bytes = File.ReadAllBytes(file.FullName);
        return Task.FromResult<SourceText?>(new(FrontMatter(Decode(bytes)).Body.Trim(), "sha256:" + Hashing.Hash(bytes)));
    }

    static string Decode(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿').Replace("\r\n", "\n");
    // A plain excerpt: heading marks dropped, links shown as the text a reader sees.
    static string Overview(string body)
    {
        body = WikiLink().Replace(body, m => m.Value.Split('|') is [_, var shown] ? shown.TrimEnd(']') : m.Groups["target"].Value);
        body = MarkdownLink().Replace(body, m => m.Value[1..m.Value.IndexOf(']')]);
        var text = Whitespace().Replace(HeadingMarks().Replace(body, ""), " ").Trim();
        return text.Length <= OverviewText ? text : text[..(OverviewText - 1)] + "…";
    }

    // Link targets, resolved the way Obsidian does: a path (from the note's folder, then from the root), else a file
    // name (the one in the same folder, then the shortest path), else an alias that only one note has.
    internal sealed class Index(IReadOnlyList<Note> notes)
    {
        readonly Dictionary<string, string> paths = notes.GroupBy(n => n.Path.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Path);
        readonly ILookup<string, string> names = notes.ToLookup(n => Stem(n.Path), n => n.Path);
        readonly ILookup<string, string> aliases = notes.SelectMany(n => n.Aliases.Select(a => (Alias: a.ToLowerInvariant(), n.Path)))
            .Distinct().ToLookup(x => x.Alias, x => x.Path);

        public string? Resolve(Link link, string from)
        {
            var target = link.Target.Replace('\\', '/').Trim();
            var here = Folder(from);
            if (!link.Wiki) return Path(Combine(here, target)) ?? Path(Combine(here, target + ".md"));
            var bare = target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? target[..^3] : target;
            if (bare.Contains('/'))
                return Path(Combine(here, bare + ".md")) ?? Path(Combine(null, bare + ".md"))
                    ?? Unique(paths.Values.Where(p => p.EndsWith("/" + bare + ".md", StringComparison.OrdinalIgnoreCase)));
            var named = names[bare.ToLowerInvariant()].ToArray();
            if (named.Length > 0)
                return named.FirstOrDefault(p => Folder(p) == here)
                    ?? named.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).First();
            return Unique(aliases[bare.ToLowerInvariant()]);
        }
        string? Path(string? relative) => relative is not null && paths.TryGetValue(relative.ToLowerInvariant(), out var found) ? found : null;
        static string? Unique(IEnumerable<string> found) => found.Distinct().ToArray() is [var only] ? only : null;
        static string Stem(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        // A relative path from a folder, with "." and ".." applied; null when it climbs out of the notes folder.
        static string? Combine(string? folder, string relative)
        {
            if (relative.StartsWith('/')) relative = relative.TrimStart('/');
            var parts = new List<string>(folder?.Split('/') ?? []);
            foreach (var part in relative.Split('/'))
            {
                if (part is "" or ".") continue;
                if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            return parts.Count == 0 ? null : string.Join('/', parts);
        }
    }

    [GeneratedRegex(@"^\s*(```|~~~)")] private static partial Regex FencePattern();
    [GeneratedRegex("`[^`\n]*`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"!?\[\[(?<target>[^\[\]|#^]*)(?:[#^][^\[\]|]*)?(?:\|[^\[\]]*)?\]\]")] private static partial Regex WikiLink();
    [GeneratedRegex(@"(?<!!)\[[^\]\n]*\]\((?:<(?<angle>[^>\n]+)>|(?<plain>[^)\s]+))(?:\s+""[^""\n]*"")?\)")] private static partial Regex MarkdownLink();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")] private static partial Regex SchemePattern();
    [GeneratedRegex(@"(?<=^|\s)#([\p{L}\p{N}_/-]+)")] private static partial Regex TagPattern();
    // A tag has a letter or symbol somewhere: #2026 is a number, not a tag.
    [GeneratedRegex(@"^[\p{L}\p{N}_/-]*[\p{L}_/-][\p{L}\p{N}_/-]*$")] private static partial Regex TagName();
    [GeneratedRegex(@"^(---|\.\.\.)[ \t]*\r?$", RegexOptions.Multiline)] private static partial Regex FrontEnd();
    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+")] private static partial Regex HeadingMarks();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
