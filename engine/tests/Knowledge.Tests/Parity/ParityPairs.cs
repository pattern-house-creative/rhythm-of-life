using System.Text.Json;

namespace Knowledge.Tests.Parity;

// The labelled pairs (pairs.json): two records each, labelled duplicate, near-duplicate, contradiction, related,
// unrelated or insight by the rules written in the file, for re-tuning the similarity, duplicate and insight
// thresholds per model (SB-P2-13) and judging map relevance (SB-P4-12). A member is a corpus text record, or an
// incoming save: a text compared with what is stored, as a duplicate check on save sees it, never loaded itself.
// All of it is DEMO data.
public sealed record ParityIncoming(string Id, string Scope, string Author, DateTimeOffset At, string Text, string? Language = null, string? Note = null);
// `Resolved` is set on contradictions only: whether the corpus already settles them (a supersedes link). `Hard` marks
// a pair a similarity threshold is likely to get wrong.
public sealed record ParityPair(string Id, string A, string B, string Label, string Why, bool? Resolved = null, bool Hard = false);
// One side of a pair, whichever kind it is: its scope, text and when it was written (a memory's creation, a note's
// last change, a document's current revision, an incoming save's time).
public sealed record ParityMember(string Id, string Kind, string Scope, string Text, DateTimeOffset Written);

// The standing cases (standing-cases.json, SB-P5-12): standing instructions and the questions labelled for each, fire,
// near (shares words or a topic, about something else) or intent (its subject, another purpose). Text is null only for
// the corpus's own standing memory, whose words are the corpus's. Also: a pair of an instruction and another's question
// or a golden query (Golden names it) that is no negative, labelled fire or intent, read by meaning.
public sealed record ParityStandingInstruction(string Id, string Scope, string? Text, string[] Fire, string[] Near, string[] Intent);
public sealed record ParityStandingAlso(string Instruction, string? Query, string? Golden, string Label, string Why);
public sealed record ParityStanding(string Schema, string Label, Dictionary<string, string> Labels, string[] Rules,
    ParityStandingInstruction[] Instructions, ParityStandingAlso[] Also)
{
    public static ParityStanding Load() =>
        JsonSerializer.Deserialize<ParityStanding>(File.ReadAllText(Path.Combine(ParityCorpus.Folder, "standing-cases.json")), ParityCorpus.Json)!;
}

public sealed record ParityPairs(string Schema, string Label, Dictionary<string, string> Labels, string[] Rules,
    ParityIncoming[] Incoming, ParityPair[] Pairs)
{
    public static readonly string[] Kinds = ["duplicate", "near-duplicate", "contradiction", "related", "unrelated", "insight"];
    // Insight candidates are pairs written at least this far apart (upstream's 30 days, D-028).
    public static readonly TimeSpan InsightGap = TimeSpan.FromDays(30);

    public static ParityPairs Load(string file = "pairs.json") =>
        JsonSerializer.Deserialize<ParityPairs>(File.ReadAllText(Path.Combine(ParityCorpus.Folder, file)), ParityCorpus.Json)!;

    // More labelled pairs for insight candidates (SB-P5-05): two native memories of one scope each, written 30 or more days
    // apart, labelled insight, related or unrelated; none of them in pairs.json.
    public static ParityPairs Insight() => Load("insight-pairs.json");

    public IReadOnlyDictionary<string, ParityMember> Members(ParityCorpus corpus) =>
        corpus.Memories.Select(m => new ParityMember(m.Id, "memory", m.Scope, m.Text, m.CreatedAt))
            .Concat(corpus.Notes.Select(n => new ParityMember(n.Id, "note", n.Scope, n.Text, n.ModifiedAt)))
            .Concat(corpus.Documents.Select(d => new ParityMember(d.Id, "document", d.Scope, d.Title + "\n" + d.Text, d.Source.ModifiedAt)))
            .Concat(Incoming.Select(i => new ParityMember(i.Id, "incoming", i.Scope, i.Text, i.At)))
            .ToDictionary(m => m.Id, StringComparer.Ordinal);
}
