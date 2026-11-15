using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Adapters.SecondBrain;

namespace Knowledge.Tests.Parity;

// The titles-model evaluation's labelled set (titles-set.json) and D-027's four-point rubric
// (docs/dev/second-brain-parity/titles-eval.md). One scorer serves the excerpt a star shows today, every model title and
// the reference titles, so the current setting and each candidate are judged by the same rules.
public sealed record TitlesItem(string Id, string Scope, string Author, string Lane, string Lang, string Text, string Reference,
    string[] Subject, string[] Private, string[] Accept);

public sealed record TitlesSet(string Schema, string Label, string Rule, Dictionary<string, string> Lanes, TitlesItem[] Items)
{
    public static TitlesSet Load() =>
        JsonSerializer.Deserialize<TitlesSet>(File.ReadAllText(Path.Combine(ParityCorpus.Folder, "titles-set.json")), ParityCorpus.Json)!;
}

// A reviewer's decision after a run (titles-review.json), applied to every candidate alike. With Word: a word the
// support check flagged that only restates the text (criterion "accurate"). With Title: one exact title, for item Item,
// that a lexical check misjudges (criterion "accurate", "specific" or "private"), for example a swapped person.
public sealed record TitlesJudgement(string Item, string? Word, string? Title, string Criterion, bool Pass, string Reason);

public sealed record TitlesReview(string Note, TitlesJudgement[] Judgements, string Label = "")
{
    public static readonly TitlesReview None = new("", []);
    public static string Path => System.IO.Path.Combine(ParityCorpus.Folder, "titles-review.json");
    public static TitlesReview Load() =>
        File.Exists(Path) ? JsonSerializer.Deserialize<TitlesReview>(File.ReadAllText(Path), ParityCorpus.Json)! : None;
}

// One point each: accurate (every word that makes a claim is in the text, or restates it), specific (names the text's
// subject), at most 80 characters, and private (none of the item's private details, which its first line does not show).
public sealed record TitleScore(bool Accurate, bool Specific, bool Short, bool Discreet, IReadOnlyList<string> Unsupported, IReadOnlyList<string> Leaked)
{
    public int Points => (Accurate ? 1 : 0) + (Specific ? 1 : 0) + (Short ? 1 : 0) + (Discreet ? 1 : 0);
    // The criteria a title missed, as letters: a(ccurate), s(pecific), l(ength), p(rivate).
    public string Missed => string.Concat(Accurate ? "" : "a", Specific ? "" : "s", Short ? "" : "l", Discreet ? "" : "p");
}

public static partial class TitlesRubric
{
    public const int MaxLength = 80;

    // The title a star shows today: with no titles model, the Second Brain adapter's own excerpt. It is also all the
    // titles model is given today, since the core asks for a title only when a star's title equals its overview.
    public static string Excerpt(TitlesItem item) => SecondBrainAdapter.Title(item.Text);
    public static string FirstLine(string text) => text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).First(l => l.Length > 0);

    public static TitleScore Score(TitlesItem item, string title, TitlesReview? review = null)
    {
        review ??= TitlesReview.None;
        var words = Tokens(title).ToArray();
        var text = Tokens(item.Text).ToArray();
        var restated = item.Accept.Concat(review.Judgements.Where(j => j.Item == item.Id && j.Word is not null && j.Criterion == "accurate" && j.Pass)
            .Select(j => j.Word!)).SelectMany(Tokens).ToArray();
        var unsupported = words.Where(w => !Stop.Contains(w) && !Frame.Contains(w) && !text.Any(t => Same(w, t)) && !restated.Any(a => Same(w, a)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var specific = item.Subject.Select(Content).Any(parts => parts.Length > 0 && parts.All(p => words.Any(w => Same(w, p))));
        var leaked = item.Private.Where(p => Tokens(p).ToArray() is { Length: > 0 } parts && parts.All(x => words.Any(w => Exact(w, x)))).ToArray();
        var score = new TitleScore(unsupported.Length == 0, specific, title.Length is > 0 and <= MaxLength, leaked.Length == 0, unsupported, leaked);
        foreach (var j in review.Judgements.Where(j => j.Item == item.Id && j.Title is not null && j.Title == title))
            score = j.Criterion switch
            {
                "accurate" => score with { Accurate = j.Pass },
                "specific" => score with { Specific = j.Pass },
                "private" => score with { Discreet = j.Pass },
                _ => throw new InvalidDataException($"titles-review.json: unknown criterion {j.Criterion}"),
            };
        return score;
    }

    // Overlap of content words with the reference title (F1), for information only: the rubric does not use it.
    public static double Overlap(string title, string reference)
    {
        var t = Tokens(title).Where(w => !Stop.Contains(w) && !Frame.Contains(w)).Distinct().ToArray();
        var r = Tokens(reference).Where(w => !Stop.Contains(w) && !Frame.Contains(w)).Distinct().ToArray();
        if (t.Length == 0 || r.Length == 0) return 0;
        double precision = (double)t.Count(w => r.Any(x => Same(w, x))) / t.Length, recall = (double)r.Count(x => t.Any(w => Same(w, x))) / r.Length;
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    // Words, folded: lower case, no accents, "13th" as 13, "Sam's" as sam, month and day abbreviations spelled out,
    // and no one-letter words.
    public static IEnumerable<string> Tokens(string s)
    {
        var folded = new StringBuilder(s.Length);
        foreach (var c in s.Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        var text = Possessive().Replace(Ordinal().Replace(folded.ToString().Normalize(NormalizationForm.FormC), "$1"), "");
        foreach (Match m in Word().Matches(text))
            if (m.Value.Length > 1 || char.IsDigit(m.Value[0])) yield return Abbreviations.GetValueOrDefault(m.Value, m.Value);
    }

    static string[] Content(string phrase) => Tokens(phrase).Where(w => !Stop.Contains(w)).ToArray();

    // The same word: equal, or equal after a light stemming, or one stem starting the other when both have at least
    // five letters ("renew" and "renewal"). Numbers match only exactly.
    public static bool Same(string a, string b)
    {
        if (a == b) return true;
        if (a.All(char.IsDigit) || b.All(char.IsDigit)) return false;
        var (x, y) = (Stem(a), Stem(b));
        return x == y || Math.Min(x.Length, y.Length) >= 5 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal));
    }

    // A private detail is matched word for word (a plural aside), so a near spelling of a code or a name still counts.
    static bool Exact(string word, string detail) => word == detail || word == detail + "s" || detail == word + "s";

    static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w[..^3] + "y";
        else if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && (w.EndsWith("ed", StringComparison.Ordinal) || w.EndsWith("es", StringComparison.Ordinal))) w = w[..^2];
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
        return w.Length > 3 && w.EndsWith('e') ? w[..^1] : w;
    }

    // Words that make no claim: function words in the set's four languages (folded, so "für" is "fur").
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "an", "the", "and", "or", "but", "of", "to", "in", "on", "at", "for", "by", "with", "from", "about", "as", "is", "are", "was",
        "were", "be", "been", "it", "its", "this", "that", "these", "those", "into", "onto", "up", "out", "off", "so", "if", "my", "your",
        "our", "their", "his", "her", "he", "she", "they", "we", "you", "me", "us", "them", "do", "does", "did", "has", "have", "had",
        "will", "would", "can", "could", "should", "may", "might", "must", "just", "also", "too", "via", "per",
        "le", "la", "les", "de", "du", "des", "un", "une", "et", "ou", "au", "aux", "pour", "en", "sur", "sous", "avec", "par", "ce",
        "cette", "ces", "est", "qu", "que", "qui", "il", "elle",
        "der", "die", "das", "den", "dem", "ein", "eine", "einen", "einem", "einer", "und", "oder", "am", "im", "an", "auf", "fur",
        "mit", "von", "vom", "zum", "zur", "zu", "ist", "bei",
        "el", "los", "las", "lo", "del", "con", "para", "por", "una", "es", "se", "su", "al",
    };

    // Words that frame a title without saying anything about the text ("note", "reminder", "list"). A title of frame
    // words alone names no subject, so it fails "specific", not "accurate".
    static readonly HashSet<string> Frame = new(StringComparer.Ordinal)
    {
        "note", "notes", "memo", "reminder", "reminders", "list", "lists", "checklist", "todo", "task", "tasks", "summary", "overview",
        "details", "info", "information", "update", "updates", "idea", "ideas", "thought", "thoughts", "misc", "log", "recap", "things",
        "nota", "notas", "lista", "recordatorio", "notiz", "notizen", "liste", "erinnerung", "rappel",
    };

    static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["jan"] = "january", ["feb"] = "february", ["mar"] = "march", ["apr"] = "april", ["jun"] = "june", ["jul"] = "july",
        ["aug"] = "august", ["sep"] = "september", ["sept"] = "september", ["oct"] = "october", ["nov"] = "november", ["dec"] = "december",
        ["mon"] = "monday", ["tue"] = "tuesday", ["tues"] = "tuesday", ["wed"] = "wednesday", ["thu"] = "thursday", ["thur"] = "thursday",
        ["thurs"] = "thursday", ["fri"] = "friday", ["sat"] = "saturday", ["sun"] = "sunday",
    };

    [GeneratedRegex(@"\p{L}+|\p{N}+")] private static partial Regex Word();
    [GeneratedRegex(@"(\d)(st|nd|rd|th)\b")] private static partial Regex Ordinal();
    [GeneratedRegex(@"(?<=\p{L})'s\b")] private static partial Regex Possessive();
}
