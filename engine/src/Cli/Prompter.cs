namespace Knowledge.Cli;

// Questions for `init` and `connect`. On a terminal secrets are read without echo; otherwise each answer is one line
// of input, so scripts and tests can pipe answers in. With --no-input every question takes its default, and one with
// no default stops the command naming the flag that would answer it.
public sealed class Prompter(TextReader input, TextWriter output, bool noInput, bool terminal)
{
    public TextReader Input { get; } = input;
    public TextWriter Output { get; } = output;

    public static Prompter Console(bool noInput) =>
        new(System.Console.In, System.Console.Out, noInput, !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected);

    // An answer, or `fallback` for an empty one. `flag` names the option that answers it without a prompt.
    public string Ask(string question, string? fallback, string flag, Func<string, string?>? problem = null)
    {
        while (true)
        {
            string? answer;
            if (noInput) answer = fallback ?? throw new ArgumentException($"{question}: no default; give {flag}");
            else
            {
                Output.Write(fallback is { Length: > 0 } ? $"{question} [{fallback}]: " : $"{question}: ");
                answer = Input.ReadLine();
                if (answer is null) answer = fallback ?? throw new ArgumentException($"{question}: no answer; give {flag}");
                answer = answer.Trim();
                if (answer.Length == 0) answer = fallback ?? "";
            }
            var wrong = problem?.Invoke(answer);
            if (wrong is null) return answer;
            if (noInput || !terminal) throw new ArgumentException($"{flag}: {wrong}");
            Output.WriteLine($"  {wrong}");
        }
    }

    public bool Confirm(string question, bool fallback, string flag)
    {
        var answer = Ask($"{question} (y/n)", fallback ? "y" : "n", flag, a => Yes(a) is null ? "answer y or n" : null);
        return Yes(answer)!.Value;
    }
    public static bool? Yes(string answer) => answer.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "on" => true,
        "n" or "no" or "false" or "off" => false,
        _ => null,
    };

    // A secret: never echoed, never a default, never in an argument. An optional one may be left empty ("").
    public string Secret(string question, bool optional = false)
    {
        if (noInput) throw new ArgumentException($"{question}: a secret has to be typed or piped in; run without --no-input");
        Output.Write($"{question} (input hidden): ");
        string? answer;
        if (terminal && ReferenceEquals(Input, System.Console.In))
        {
            var typed = new System.Text.StringBuilder();
            for (var key = System.Console.ReadKey(true); key.Key != ConsoleKey.Enter; key = System.Console.ReadKey(true))
                if (key.Key == ConsoleKey.Backspace) { if (typed.Length > 0) typed.Length--; }
                else if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
            Output.WriteLine();
            answer = typed.ToString();
        }
        else
        {
            answer = Input.ReadLine();
            Output.WriteLine();
        }
        var value = answer?.Trim() ?? "";
        return value.Length > 0 || optional ? value : throw new ArgumentException($"{question}: nothing was entered");
    }
}

// Command-line options: `--name value`, repeatable ones, `--flag`, and positionals. An unknown option is an error.
public sealed class CliArgs
{
    readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
    readonly HashSet<string> flags = new(StringComparer.Ordinal);
    public List<string> Positionals { get; } = [];

    public CliArgs(IReadOnlyList<string> args, IReadOnlyCollection<string> options, IReadOnlyCollection<string> switches)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { Positionals.Add(arg); continue; }
            var (name, inline) = arg.IndexOf('=') is var eq and > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
            if (switches.Contains(name) && inline is null) { flags.Add(name); continue; }
            if (!options.Contains(name)) throw new ArgumentException($"unknown option {name}");
            var value = inline ?? (i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value"));
            (values.TryGetValue(name, out var list) ? list : values[name] = []).Add(value);
        }
    }
    public string? this[string name] => values.TryGetValue(name, out var list) ? list[^1] : null;
    public IReadOnlyList<string> All(string name) => values.TryGetValue(name, out var list) ? list : [];
    public bool Has(string name) => flags.Contains(name);
}
