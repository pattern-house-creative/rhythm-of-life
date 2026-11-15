namespace Knowledge.Adapters;

// A folder mounted read-only into the engine that an adapter reads (the notes folder, the import folder). Hidden
// entries (a name starting with '.', such as .obsidian, .git or .trash) and links are skipped, and a relative path is
// resolved only to a plain file inside the folder, so nothing outside it is ever read through it. Paths are relative,
// with '/'.
public sealed class SourceFolder
{
    const int MaxDepth = 12, MaxEntries = 20_000;
    public string Root { get; }
    public SourceFolder(string root, string setting)
    {
        if (!Path.IsPathRooted(root)) throw new ConfigException($"{setting} must be an absolute path inside the engine container");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public sealed record Entry(string Path, string Full, long Length, DateTimeOffset Modified);

    public bool Exists => Directory.Exists(Root) && !Linked(new DirectoryInfo(Root));

    // Every wanted file, in path order. A missing folder throws, so a source that is not mounted changes nothing
    // instead of looking empty. `skip` names top-level folders other adapters own.
    public IReadOnlyList<Entry> Files(Func<string, bool> wanted, string[]? skip = null)
    {
        if (!Exists) throw new DirectoryNotFoundException("The folder is not there; check the mount.");
        var found = new List<Entry>();
        var pending = new Stack<(DirectoryInfo Dir, string Relative, int Depth)>();
        pending.Push((new DirectoryInfo(Root), "", 0));
        var seen = 0;
        while (pending.Count > 0)
        {
            var (dir, relative, depth) = pending.Pop();
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (++seen > MaxEntries) throw new IOException("The folder holds too many entries");
                if (info.Name.StartsWith('.') || Linked(info)) continue;
                var path = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
                if (info is DirectoryInfo sub)
                {
                    if (depth < MaxDepth && !(depth == 0 && skip is not null && skip.Contains(info.Name, StringComparer.OrdinalIgnoreCase)))
                        pending.Push((sub, path, depth + 1));
                    continue;
                }
                if (info is FileInfo file && wanted(path))
                    found.Add(new(path, file.FullName, file.Length, Clamp(file.LastWriteTimeUtc)));
            }
        }
        return found.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    // The file a relative path names, or null when it is not a plain file inside the folder (no "..", no hidden part,
    // no link on the way).
    public FileInfo? Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 1000 || relative.Contains('\\') || relative.Contains('\0')) return null;
        var parts = relative.Split('/');
        if (parts.Any(p => p.Length == 0 || p.StartsWith('.'))) return null;
        var current = Root;
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            var dir = new DirectoryInfo(current);
            if (!dir.Exists || Linked(dir)) return null;
        }
        var file = new FileInfo(Path.Combine(current, parts[^1]));
        if (!file.Exists || Linked(file) || !file.FullName.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return file;
    }

    static bool Linked(FileSystemInfo info) => info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    // A clock in the future (a copied file, a skewed host) reads as now, so records never claim a later change.
    static DateTimeOffset Clamp(DateTime utc)
    {
        var at = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var now = DateTimeOffset.UtcNow;
        return at > now ? now : at;
    }
}
