using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Adapters;

namespace Knowledge.Cli;

// `rhythm connect [<adapter>]` and `rhythm disconnect <adapter>`: one integration at a time, driven by its descriptor
// (contracts/adapters/<name>.json). Settings go to adapters.<name> in rhythm.yaml, secrets to
// secrets/adapters/<name>_<purpose> (mode 0600, owned by the engine's user), a worker's profile to COMPOSE_PROFILES.
// rhythm.yaml and .env change only once everything else, the adapter's own connect step included, has worked.
public static partial class ConnectCommand
{
    public static async Task<int> Run(DeployFolder deploy, IReadOnlyList<string> args, Prompter? prompter = null,
        IEnumerable<Func<IAdapter>>? known = null, string? descriptors = null, CancellationToken ct = default)
    {
        var a = new CliArgs(args, ["--set"], ["--no-input"]);
        var all = AdapterDescriptor.Load(descriptors);
        var (yaml, config) = Settings(deploy);
        var ask = prompter ?? Prompter.Console(a.Has("--no-input"));
        var o = ask.Output;
        if (a.Positionals.Count == 0)
        {
            o.WriteLine(all.Count == 0 ? "No adapters are described in this build." : "Adapters (rhythm connect <name>):");
            foreach (var d in all)
                o.WriteLine($"  {d.Name,-14} {(config.AdapterEnabled(d.Name) ? "on " : "off")}  {d.Title}{(d.Unofficial ? " (unofficial)" : "")}: {d.Summary}");
            return 0;
        }
        var descriptor = all.FirstOrDefault(d => d.Name == a.Positionals[0])
            ?? throw new ArgumentException($"No adapter named {a.Positionals[0]}; `rhythm connect` lists them.");
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in a.All("--set"))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || descriptor.Settings.All(s => s.Key != pair[..eq])) throw new ArgumentException($"--set {pair}: {descriptor.Name} has no such setting");
            given[pair[..eq]] = pair[(eq + 1)..];
        }

        o.WriteLine($"{descriptor.Title}{(descriptor.Unofficial ? " (unofficial: it relies on an undocumented vendor interface)" : "")}: {descriptor.Summary}");
        o.WriteLine($"What to set up outside Rhythm first: {descriptor.Docs}");
        // Its container starts as soon as this turns it on, and Docker would make a folder where a missing token belongs.
        var token = deploy.Secret($"harness_{descriptor.Name}_token");
        if (descriptor.HarnessToken && !File.Exists(token))
            throw new ArgumentException(Directory.Exists(token)
                ? $"{deploy.Shown(token)} is a folder Docker made: remove it, then, with the stack running, ./rhythm token create {descriptor.Name}"
                : $"{descriptor.Name} needs its own harness token first. With the stack running: ./rhythm token create {descriptor.Name}");
        var current = config.Adapter(descriptor.Name);
        var expected = new List<(string Path, string Yaml, string Value)>();
        foreach (var setting in descriptor.Settings)
        {
            var fallback = setting.Type == "list" && current.List(setting.Key) is { Count: > 0 } items ? string.Join(", ", items)
                : current.String(setting.Key) ?? DefaultText(setting.Default);
            var answer = given.TryGetValue(setting.Key, out var value)
                ? (Problem(setting, value, config) is { } wrong ? throw new ArgumentException($"--set {setting.Key}: {wrong}") : value)
                : ask.Ask(setting.Prompt + (setting.Help is null ? "" : $" ({setting.Help})"), fallback, $"--set {setting.Key}=...", v => Problem(setting, v, config));
            if (answer.Length == 0) continue;
            var (text, readBack) = Yaml(setting, answer);
            expected.Add(($"adapters.{descriptor.Name}.{setting.Key}", text, readBack));
        }
        expected.Add(($"adapters.{descriptor.Name}.enabled", "true", "true"));

        Owners.Folder(deploy.AdapterSecrets, Owners.AdapterFolder);
        foreach (var secret in descriptor.Secrets.Where(s => s.Source != "connect"))
        {
            var path = deploy.AdapterSecret(descriptor.Name, secret.Purpose);
            if (Directory.Exists(path)) throw new ArgumentException($"{deploy.Shown(path)} is a folder; remove it and connect again");
            var exists = File.Exists(path) && new FileInfo(path).Length > 0;
            if (secret.Source == "generate") { if (!exists) Owners.WriteNew(path, Principals.NewToken(), Owners.AdapterSecret(path)); continue; }
            if (exists && (a.Has("--no-input") || ask.Confirm($"{deploy.Shown(path)} is already there. Keep it?", true, "--no-input"))) continue;
            if (!secret.Required && a.Has("--no-input")) continue;
            var pasted = ask.Secret(secret.Prompt + (secret.Help is null ? "" : $" ({secret.Help})"), !secret.Required);
            if (pasted.Length == 0) continue; // an optional one left empty: nothing written
            if (exists) Owners.Rewrite(path, pasted, Owners.AdapterSecret(path)); else Owners.WriteNew(path, pasted, Owners.AdapterSecret(path));
        }

        foreach (var (path, text, _) in expected) yaml = YamlText.Set(yaml, path, text);
        var updated = Verify(yaml, expected.Select(e => (e.Path, e.Value)));
        if (descriptor.Connect)
        {
            var adapter = (known ?? AdapterCatalog.Known).Select(make => make()).OfType<IAdapterConnect>().FirstOrDefault(x => x.Name == descriptor.Name)
                ?? throw new ConfigException($"{descriptor.Name} needs a connect step that this engine build does not have");
            var code = await adapter.Connect(new(updated.Adapter(descriptor.Name), updated, deploy.AdapterSecrets, ask.Input, o, [.. a.Positionals.Skip(1)]), ct);
            foreach (var file in Directory.GetFiles(deploy.AdapterSecrets, descriptor.Name + "_*")) Owners.Apply(file, Owners.AdapterSecret(file));
            if (code != 0) { o.WriteLine($"{descriptor.Title} was not connected; rhythm.yaml is unchanged."); return code; }
        }
        if (descriptor.Secrets.FirstOrDefault(s => s.Required && !File.Exists(deploy.AdapterSecret(descriptor.Name, s.Purpose))) is { } missing)
            throw new ArgumentException($"{deploy.Shown(deploy.AdapterSecret(descriptor.Name, missing.Purpose))} was not written; rhythm.yaml is unchanged");

        Owners.Rewrite(deploy.Settings, yaml, Owners.Person(deploy, Owners.Readable));
        if (descriptor.Profile is { } profile)
            Owners.Rewrite(deploy.Env, EnvFile.Profiles(File.Exists(deploy.Env) ? File.ReadAllText(deploy.Env) : "", profile, true), Owners.Person(deploy, Owners.Readable));
        o.WriteLine($"{descriptor.Title} is on{(descriptor.Profile is null ? "" : $", with the compose profile {descriptor.Profile}")}. "
            + "The engine applies it when it restarts (./rhythm connect restarts a running stack for you).");
        return 0;
    }

    public static int Disconnect(DeployFolder deploy, IReadOnlyList<string> args, TextWriter o, string? descriptors = null)
    {
        var a = new CliArgs(args, [], []);
        if (a.Positionals.Count != 1) throw new ArgumentException("disconnect <adapter>");
        var name = a.Positionals[0];
        var (yaml, config) = Settings(deploy);
        var descriptor = AdapterDescriptor.Load(descriptors).FirstOrDefault(d => d.Name == name);
        if (descriptor is null && !config.Adapters.ContainsKey(name)) throw new ArgumentException($"No adapter named {name} in this install.");
        if (config.AdapterEnabled(name))
        {
            yaml = YamlText.Set(yaml, $"adapters.{name}.enabled", "false");
            Verify(yaml, [($"adapters.{name}.enabled", "false")]);
            Owners.Rewrite(deploy.Settings, yaml, Owners.Person(deploy, Owners.Readable));
        }
        if (descriptor?.Profile is { } profile && File.Exists(deploy.Env))
            Owners.Rewrite(deploy.Env, EnvFile.Profiles(File.ReadAllText(deploy.Env), profile, false), Owners.Person(deploy, Owners.Readable));
        o.WriteLine($"{name} is off. Its secret files stay in {deploy.Shown(deploy.AdapterSecrets)}/; delete them yourself when you no longer need them.");
        return 0;
    }

    public static (string Yaml, RhythmConfig Config) Settings(DeployFolder deploy)
    {
        if (!File.Exists(deploy.Settings)) throw new ArgumentException("No rhythm.yaml yet: run ./rhythm init first.");
        var yaml = File.ReadAllText(deploy.Settings).Replace("\r\n", "\n");
        return (yaml, RhythmConfig.Parse(yaml));
    }

    static RhythmConfig Verify(string yaml, IEnumerable<(string Path, string Value)> expected)
    {
        var config = RhythmConfig.Parse(yaml);
        foreach (var (path, value) in expected)
            if (YamlText.Get(yaml, path) != value) throw new ConfigException($"could not write {path} into rhythm.yaml safely; edit it by hand");
        return config;
    }

    static string DefaultText(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.Array } v => string.Join(", ", v.EnumerateArray().Select(i => i.ToString())),
        _ => "",
    };

    // Why an answer does not fit the setting's type, or null. An empty answer is fine unless the setting is required.
    static string? Problem(AdapterDescriptor.Setting setting, string answer, RhythmConfig config)
    {
        if (answer.Length == 0) return setting.Required ? "required" : null;
        return setting.Type switch
        {
            "int" => int.TryParse(answer, out _) ? null : "a whole number",
            "bool" => Prompter.Yes(answer) is null ? "yes or no" : null,
            "person" => config.IsSubject(answer) ? null : $"one of {string.Join(", ", config.Subjects)}",
            "path" => answer.StartsWith('/') && !answer.Any(char.IsWhiteSpace) ? null : "a path inside the engine container, starting with /",
            "url" => Url().IsMatch(answer) ? null : "an http:// or https:// address",
            "list" => answer.Length > 2000 ? "too long" : null,
            _ => answer.Length > 500 || answer.Contains('\n') ? "one line, at most 500 characters" : null,
        } ?? (setting.Pattern is { } pattern && (setting.Type == "list" ? Items(answer) : [answer]).Any(a => !Regex.IsMatch(a, pattern))
            ? "not in the form this setting takes" : null);
    }
    static string[] Items(string answer) => answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    // The YAML text written for an answer, and what reading it back must give.
    static (string Text, string ReadBack) Yaml(AdapterDescriptor.Setting setting, string answer)
    {
        switch (setting.Type)
        {
            case "int": var n = int.Parse(answer); return (YamlText.Scalar(n), n.ToString());
            case "bool": var b = Prompter.Yes(answer)!.Value; return (YamlText.Scalar(b), b ? "true" : "false");
            case "list":
                var items = Items(answer);
                return (YamlText.List(items), string.Join(',', items));
            default: return (YamlText.Scalar(answer), answer);
        }
    }

    [GeneratedRegex(@"^https?://\S+$")] private static partial Regex Url();
}
