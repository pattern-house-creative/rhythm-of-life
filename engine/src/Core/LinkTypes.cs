namespace Knowledge.Relationships;

// The link vocabulary (SB-P4-01, D-030; docs/dev/memory.md section 8): Second Brain's eight edge types beside Rhythm's own,
// each with its direction and the memory kinds it may join. A link's `type` stays open text (001's relationships, 025's
// made_links): a type not listed here is a source's own word, kept with the direction the source states. Rhythm's own
// types keep their names and meaning; upstream's are spelled as upstream spells them, so imports and AI clients match.
// Migration 051's link_types() answers the same list for the checked functions that make a link, and the app names each
// in plain words (app/src/knowledge.ts); LinkTypeTests holds the three together.
//
// What a link carries beside its type, in either store: who made it as a verification (verified, inferred, suggested;
// upstream's provenance maps onto it, Verification and Provenance below), its evidence, a confidence from 0 to 1 (the
// map's Strength; made_links.confidence), the revision of its source end it was made from (the map's SourceRevision;
// made_links.source_revision) and when it was last checked against its source (relationships.checked_at;
// made_links.checked_at).
public sealed record LinkType(string Name, bool Directed, IReadOnlyList<string>? Kinds, bool Upstream);

public static class LinkTypes
{
    public const string Episodic = "episodic", Semantic = "semantic";
    static readonly string[] EpisodicOnly = [Episodic];

    public static readonly IReadOnlyList<LinkType> All =
    [
        // Second Brain's (src/graph/types.ts at the pinned commit): one undirected, two that join episodic memories only.
        new("relates_to", false, null, true),
        new("supersedes", true, null, true),
        new("caused_by", true, null, true),
        new("decided", true, EpisodicOnly, true),
        new("about_person", true, null, true),
        new("part_of_project", true, null, true),
        new("follows", true, EpisodicOnly, true),
        new("drawn_from", true, null, true),
        // Rhythm's own, as its engines and adapters make them today.
        new("references", true, null, false),  // an exact reference in a record's body (RelationshipDiscovery)
        new("in_project", true, null, false),  // a memory in its project (MemoryStars, Second Brain, notes)
        new("in_folder", true, null, false),   // a Drive file in its folder
        new("about", false, null, false),      // a record that names a data star's topic (RelationshipCatalog)
        new("similar_to", false, null, false), // two records alike in meaning (SimilaritySuggestions)
        new("copy_of", false, null, false),    // two near-identical files (SimilaritySuggestions)
        new("contains", true, null, false),    // a document and its pages (documents ingest, off the map)
    ];
    static readonly Dictionary<string, LinkType> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static LinkType? Find(string type) => ByName.GetValueOrDefault(type);

    // The direction a link of `type` has: the vocabulary's for a listed type, else what its source states.
    public static bool Directed(string type, bool stated) => Find(type)?.Directed ?? stated;

    // The episodic rule (upstream's kindsAllowEdge, adapted): a type with kinds joins only memories of those kinds at both
    // ends, and an end of no kind (a memory not yet classified, or any record that is not a memory) never qualifies, since
    // no kind is no evidence. Every other type, an unlisted one included, joins any two records.
    public static bool Allows(string type, string? sourceKind, string? targetKind) =>
        Find(type)?.Kinds is not { } kinds
        || sourceKind is not null && targetKind is not null && kinds.Contains(sourceKind) && kinds.Contains(targetKind);

    // Upstream's provenance as Rhythm's verification: a link a person made (explicit) is verified, one the system made
    // (system) inferred, and an automatic one (inferred) or one that says nothing only suggested (D-015).
    public static string Verification(string? provenance) => provenance switch
    {
        "explicit" => "verified",
        "system" => "inferred",
        _ => "suggested",
    };
    // And back, for an answer or an export in upstream's words.
    public static string Provenance(string verification) => verification switch
    {
        "verified" => "explicit",
        "inferred" => "system",
        "suggested" => "inferred",
        _ => throw new ArgumentException("A link's verification is verified, inferred or suggested", nameof(verification)),
    };
}
