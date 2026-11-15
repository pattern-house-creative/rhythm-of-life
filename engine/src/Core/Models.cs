using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knowledge;

public record Measurement(string Metric, double Value, string Unit, DateOnly Date,
    DateTimeOffset? ObservedAt = null, string SampleKey = "daily", string? DeviceId = null, string Quality = "source-reported");
public record ResourceInput(string Subject, string Provider, string ExternalId, string Kind, string Scope,
    string Title, string? Url, string Content = "", string? Revision = null, DateTimeOffset? SourceUpdated = null,
    DateTimeOffset? AuthorizedUntil = null, DateOnly? Date = null, string? Timezone = null,
    string? RawPath = null, string? Checksum = null, string Metadata = "{}", string Status = "current",
    string? Domain = null);
public record Archived(string Path, string Checksum);
public record SyncJob(Guid Id, string Subject, DateOnly From, DateOnly Until, DateOnly Next);
public static class Hashing
{
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static string Hash(string data) => Hash(Encoding.UTF8.GetBytes(data));
}

// Never derive filesystem paths from provider IDs or supplied filenames.
public sealed class Archive(string root)
{
    public string Root { get; } = System.IO.Path.GetFullPath(root);
    public async Task<Archived> Save(byte[] bytes, CancellationToken ct = default)
    {
        var hash = Hashing.Hash(bytes);
        var relative = $"{hash[..2]}/{hash}";
        var full = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (File.Exists(full))
        {
            if (Hashing.Hash(await File.ReadAllBytesAsync(full, ct)) != hash) throw new IOException("Archive integrity failure");
            return new(relative, hash);
        }
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(true);
            }
            try { File.Move(temporary, full, false); }
            catch (IOException) when (File.Exists(full))
            {
                if (Hashing.Hash(await File.ReadAllBytesAsync(full, ct)) != hash) throw;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(relative, hash);
    }

    // One original as Save kept it, by the path and checksum the catalog recorded for it, checked against its own
    // bytes and positioned at the start. Null when the record does not name an archived file, or the file is gone.
    public async Task<FileStream?> Open(string relative, string checksum, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(relative, "\\A[0-9a-f]{2}/[0-9a-f]{64}\\z") || relative[..2] != checksum[..2] || relative[3..] != checksum)
            return null;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        FileStream stream;
        try { stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        try
        {
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != checksum) throw new IOException("Archive integrity failure");
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
