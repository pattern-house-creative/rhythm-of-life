namespace Knowledge;

// Secrets arrive as files named by *_FILE variables (Docker secrets at /run/secrets/<name>). One value per file,
// surrounding whitespace trimmed. Never logged, never echoed back.
public static class SecretFiles
{
    public static string Read(string path) => File.ReadAllText(path).Trim();

    // The secret named by `variable`, or null when it is not configured: the variable is unset, or the file is
    // missing, empty, unreadable or a directory (Docker creates an empty directory when a secret file did not exist).
    public static string? TryRead(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path) || !File.Exists(path)) return null;
        try { return Read(path) is { Length: > 0 } value ? value : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
