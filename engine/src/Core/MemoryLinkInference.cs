using System.Globalization;
using System.Text.Json;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Links on save (SB-P4-03; migration 062; docs/dev/memory.md section 8; D-015, D-194, D-200, D-211), adapted from Second
// Brain's edge inference on write (src/graph/edges.ts at the pinned commit, MIT; NOTICE). After a write that saved a
// memory's words (a new memory, an append, a change or an undo of its words or kind, a release of its hold), the memory
// store (MemoryStore.Inference) gives the memory its passages at once, as the index job would within a minute, so a save
// that follows finds it; embeds its words alone (Query: the map's recipe with no title, as ThresholdTuning scores the
// labelled pairs); and calls 062's infer_memory_links, which links it to at most 3 memories of its scope whose best
// passage scores at or above the threshold (relates_to, or follows between two episodic memories written within 30
// minutes), never across scopes, never to a held, trashed or replaced memory, never a pair someone dismissed. A link it
// writes is automatic (verification suggested, never verified): connections, the app and the terminal say so, the
// upkeep (056) invalidates and prunes it like any other, and unlink removes it and keeps a dismissal.
//
// The threshold is EmbeddingThresholds.For's LinkOnSave: today's 0.78 unless adapters.embeddings.thresholds is tuned
// (MiniLM's own, re-derived on the labelled pairs in run 19). A store has one only with memory.tools on, the live model
// loaded and adapters.embeddings.sources listing memories (For); every other store (a test's, the terminal's, which loads
// no model) writes exactly what it wrote before. Inference never fails a write: the write has committed by then, and a
// failure is logged by its kind alone and leaves the memory without links until its words change again.

// One link a save found: the other memory, the link as it stands, its score, and what this step did (made, renewed: an
// out-of-date automatic link drawn again, linked: the two are linked already, kept: a link of its type in another
// lifecycle, left as it is).
public sealed record MemoryInferredLink(Guid Id, Guid Memory, string Type, Guid From, Guid To, string Verification, string Lifecycle,
    double Score, string Outcome);

// What a step did for one memory: Skipped when the memory was no longer as written (its words moved on, it is held,
// trashed or not current), so a later change runs it again.
public sealed record MemoryInferred(Guid Memory, IReadOnlyList<MemoryInferredLink> Links, bool Skipped);

public sealed class MemoryLinkInference(Database db, IEmbedder model, double threshold, ILogger? log = null)
{
    public double Threshold => threshold;
    public string Model => model.Id;

    // The vector a save's words are compared by: the mean of their first passages, unit length, rounded (the map's
    // recipe, SimilaritySuggestions.Vector, with no title). Null for words that give none.
    public static float[]? Query(IEmbedder model, string text) => SimilaritySuggestions.Vector(model.Embed, model.Chunks, "", text);

    // For a call's store: one only with memory.tools on, the embeddings adapter on with its live model loaded (the engine's
    // Embeddings service, the model the settings name), and adapters.embeddings.sources listing memories, whose passages
    // are what a save is compared with. Else none.
    public static MemoryLinkInference? For(IServiceProvider services, RhythmConfig? config)
    {
        if (config is null || !MemorySettings.ToolsOn(config)) return null;
        var settings = EmbeddingSettings.From(config);
        if (!settings.Enabled || !settings.Sources.Contains(EmbeddingSettings.Memories)) return null;
        if (services.GetService<Embeddings>() is not { Model: { } live } embeddings || embeddings.Id != settings.Model.Id) return null;
        return new(services.GetRequiredService<Database>(), live, EmbeddingThresholds.For(settings).LinkOnSave,
            services.GetService<ILoggerFactory>()?.CreateLogger<MemoryLinkInference>());
    }

    // The step for one memory, as `principal` through `call`'s channel; null when it could not run (the memory is gone or
    // held, its words give no vector, or something failed, which is logged).
    public async Task<MemoryInferred?> Run(Guid principal, Guid memory, MemoryCall call, CancellationToken ct)
    {
        try
        {
            // Its passages now (idempotent: the index job skips a memory that has them at its words' revision).
            await new SemanticSearch(db, model, Memories).Index(memory, ct, Memories);
            string text;
            int revision;
            await using (var c = await db.Source.OpenConnectionAsync(ct))
            await using (var read = Database.Command(c, null, "SELECT content,text_revision FROM memories WHERE id=@id AND hold IS NULL", ("id", memory)))
            await using (var row = await read.ExecuteReaderAsync(ct))
            {
                if (!await row.ReadAsync(ct)) return null;
                text = row.GetString(0);
                revision = row.GetInt32(1);
            }
            if (Query(model, text) is not { } vector) return null;
            await using var conn = await db.Source.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string json;
            await using (var cmd = new NpgsqlCommand(
                "SELECT infer_memory_links(@principal,@memory,@revision,@vector,@model,@threshold,@channel,@client)::text", conn, tx))
            {
                cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
                cmd.Parameters.Add(new("memory", NpgsqlDbType.Uuid) { Value = memory });
                cmd.Parameters.Add(new("revision", NpgsqlDbType.Integer) { Value = revision });
                cmd.Parameters.Add(new("vector", NpgsqlDbType.Text) { Value = Text(vector) });
                cmd.Parameters.Add(new("model", NpgsqlDbType.Text) { Value = model.Id });
                cmd.Parameters.Add(new("threshold", NpgsqlDbType.Double) { Value = threshold });
                cmd.Parameters.Add(new("channel", NpgsqlDbType.Text) { Value = call.Channel });
                cmd.Parameters.Add(new("client", NpgsqlDbType.Text) { Value = (object?)AdminAudit.Client(call.Client) ?? DBNull.Value });
                json = (string)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await tx.CommitAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            return new(o.GetProperty("memory").GetGuid(), [.. o.GetProperty("links").EnumerateArray().Select(l => new MemoryInferredLink(
                l.GetProperty("id").GetGuid(), l.GetProperty("memory").GetGuid(), l.GetProperty("type").GetString()!, l.GetProperty("from").GetGuid(),
                l.GetProperty("to").GetGuid(), l.GetProperty("verification").GetString()!, l.GetProperty("lifecycle").GetString()!,
                l.GetProperty("score").GetDouble(), l.GetProperty("outcome").GetString()!))], o.GetProperty("skipped").GetBoolean());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The kind of failure only, never a memory's words or the database's message.
            log?.LogWarning("Links on save: the step failed ({Kind}); the memory was saved without them", e.GetType().Name);
            return null;
        }
    }

    // Only a memory's own passages: the index's memories kind (PassageSources.Memory), nothing else.
    static readonly PassageSources Memories = PassageSources.Of(false, false, true, []);

    static string Text(float[] vector) => "[" + string.Join(',', vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
