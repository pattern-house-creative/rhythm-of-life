using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Knowledge.Cli;

// One install's deploy folder (deploy/compose; /work in the cli container): rhythm.yaml, .env, secrets/ and, by
// default, data/. Compose bind-mounts these files into the services, and bind mounts keep Unix owners and modes (on
// Linux and on Docker Desktop alike), so every file the CLI writes gets the owner its reader runs as.
public sealed class DeployFolder(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Settings => Path.Combine(Root, "rhythm.yaml");
    public string Env => Path.Combine(Root, ".env");
    public string Secrets => Path.Combine(Root, "secrets");
    public string AdapterSecrets => Path.Combine(Secrets, "adapters");
    public string Secret(string name) => Path.Combine(Secrets, name);
    public string AdapterSecret(string adapter, string purpose) => Path.Combine(AdapterSecrets, $"{adapter}_{purpose}");
    // The token files written for people: `rhythm token create`'s harness_<name>_token and plugin_<name>_token, and
    // `rhythm capture connect`'s capture_<name>_token.
    public IEnumerable<string> Tokens => Directory.Exists(Secrets)
        ? Directory.GetFiles(Secrets, "harness_*_token").Concat(Directory.GetFiles(Secrets, "plugin_*_token"))
            .Concat(Directory.GetFiles(Secrets, "capture_*_token")).Order(StringComparer.Ordinal)
        : [];

    // RHYTHM_DEPLOY_DIR, else the working directory (the cli service works in /work).
    public static DeployFolder Current() =>
        new(Environment.GetEnvironmentVariable("RHYTHM_DEPLOY_DIR") is { Length: > 0 } dir ? dir : Directory.GetCurrentDirectory());

    // How a path in this folder is named in messages: as the person sees it from the repository root.
    public string Shown(string path)
    {
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : "deploy/compose/" + relative;
    }

    // The secrets `rhythm init` generates: file name, who owns it, and what it holds.
    public static readonly IReadOnlyList<string> CoreSecrets = ["postgres_password", "engine_db", "app_token", "backup_password"];
}

// Who reads what. The engine image runs as 1654, the app image as 1000 (node) and the backup image as 70 (postgres);
// Postgres itself reads its password as root before dropping privileges. The Cloudflare recipe's cloudflared and the
// OpenAI tunnel recipe's client run as 65532 and read only their own secrets. A file another service must read too gets
// that service's group and group read, never "other" access (docs/configuration.md, open question Q-050).
public static class Owners
{
    // External plugin containers read their token as uid 1000 (contracts/README.md 4.1).
    public const int Engine = 1654, App = 1000, Backup = 70, Cloudflared = 65532, OpenAiTunnel = 65532, Plugin = 1000;
    // The OpenAI tunnel's own harness token (`rhythm token create openai-tunnel`): its container sends it, no one copies it.
    public const string TunnelToken = "harness_openai-tunnel_token";
    public const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode Shared = Private | UnixFileMode.GroupRead;
    public const UnixFileMode PrivateFolder = Private | UnixFileMode.UserExecute;
    public const UnixFileMode Readable = Shared | UnixFileMode.OtherRead;
    public const UnixFileMode OpenFolder = PrivateFolder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public sealed record Rule(int Uid, int Gid, UnixFileMode Mode);

    public static Rule CoreSecret(string name) => name switch
    {
        "postgres_password" => new(Engine, Backup, Shared),   // migrate (engine image) and backup
        "app_token" => new(Engine, App, Shared),              // engine and app
        "backup_password" => new(Backup, Backup, Private),
        _ => new(Engine, Engine, Private),                    // engine_db and anything else the engine reads
    };
    // secrets/adapters/<adapter>_<purpose>: the engine's, except an exposure container's own (adapter names have no '_').
    public static Rule AdapterSecret(string file) => Path.GetFileName(file).Split('_')[0] switch
    {
        "cloudflare" => new(Cloudflared, Cloudflared, Private),
        "openai-tunnel" => new(OpenAiTunnel, OpenAiTunnel, Private),
        _ => new(Engine, Engine, Private),
    };
    public static Rule AdapterFolder => new(Engine, Engine, PrivateFolder);

    // A token file `rhythm token create` writes: a harness token is the person's, to give to their client; a plugin's
    // belongs to the plugin container that reads it (open question Q-104), and the OpenAI tunnel's to its client (Q-120).
    public static Rule Token(DeployFolder deploy, string file) => Path.GetFileName(file) switch
    {
        var name when name.StartsWith("plugin_", StringComparison.Ordinal) => new(Plugin, Plugin, Private),
        TunnelToken => new(OpenAiTunnel, OpenAiTunnel, Private),
        _ => Person(deploy, Private),
    };

    // The person who owns the deploy folder (the one who cloned the repository) owns the files they edit or read:
    // rhythm.yaml, .env, secrets/ itself and their harness tokens.
    public static Rule Person(DeployFolder deploy, UnixFileMode mode)
    {
        var (uid, gid) = OwnerOf(deploy.Root) ?? (0, 0);
        return new(uid, gid, mode);
    }

    // Applies owner and mode. Only root can give a file away, and Windows has neither: there only what can be done is.
    public static void Apply(string path, Rule rule)
    {
        if (OperatingSystem.IsWindows()) return;
        if (Environment.IsPrivilegedProcess && chown(path, rule.Uid, rule.Gid) != 0)
            throw new IOException($"could not set the owner of {Path.GetFileName(path)} (error {Marshal.GetLastPInvokeError()})");
        File.SetUnixFileMode(path, rule.Mode);
    }

    // Writes a file that must not exist yet, created private so no one else can read it even for a moment.
    public static void WriteNew(string path, string content, Rule rule)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Private;
        using (var file = new FileStream(path, options)) file.Write(Encoding.UTF8.GetBytes(content));
        Apply(path, rule);
    }

    // Replaces a file's content in place (same inode), so containers that bind-mount it see the change.
    public static void Rewrite(string path, string content, Rule rule)
    {
        if (!File.Exists(path)) { WriteNew(path, content, rule); return; }
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Apply(path, rule);
    }

    public static void Folder(string path, Rule rule)
    {
        Directory.CreateDirectory(path);
        Apply(path, rule);
    }

    // uid and gid of a path, through stat(1): the struct layout of stat(2) differs between architectures.
    public static (int Uid, int Gid)? OwnerOf(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            using var stat = Process.Start(new ProcessStartInfo("stat", ["-c", "%u %g", path]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = stat.StandardOutput.ReadToEnd().Trim().Split(' ');
            stat.WaitForExit();
            return stat.ExitCode == 0 && text.Length == 2 && int.TryParse(text[0], out var uid) && int.TryParse(text[1], out var gid) ? (uid, gid) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int owner, int group);
}

// .env: KEY=VALUE lines that Docker Compose reads. Edits keep every other line and comment as it is.
public static partial class EnvFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
            if (Assignment().Match(line) is { Success: true } m) values[m.Groups[1].Value] = Unquote(m.Groups[2].Value.Trim());
        return values;
    }
    public static Dictionary<string, string> Read(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : [];

    // Sets KEY on its first line, or appends it.
    public static string Set(string text, string key, string value)
    {
        var lines = Lines(text).ToList();
        var line = $"{key}={Quote(value)}";
        var at = lines.FindIndex(l => Assignment().Match(l) is { Success: true } m && m.Groups[1].Value == key);
        if (at >= 0) lines[at] = line;
        else
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(line);
        }
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    // COMPOSE_PROFILES as a set: add or remove one profile, keeping the others in order.
    public static string Profiles(string text, string profile, bool on)
    {
        var current = (Parse(text).GetValueOrDefault("COMPOSE_PROFILES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        current.Remove(profile);
        if (on) current.Add(profile);
        return Set(text, "COMPOSE_PROFILES", string.Join(',', current));
    }

    // Plain when it is safe; single quotes (taken literally by Compose) otherwise.
    public static string Quote(string value)
    {
        if (value.Contains('\'') || value.Contains('\n')) throw new ArgumentException("a value in .env cannot hold a quote or a new line");
        return SafeValue().IsMatch(value) ? value : $"'{value}'";
    }
    static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"') ? value[1..^1] : value;
    static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")] private static partial Regex Assignment();
    [GeneratedRegex(@"^[A-Za-z0-9_./:@,+-]*$")] private static partial Regex SafeValue();
}
