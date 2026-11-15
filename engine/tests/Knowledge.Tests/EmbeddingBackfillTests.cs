using System.Text.Json;
using Npgsql;

namespace Knowledge.Tests;

// A stand-in model for the index job, so its tests need no model files: passages of 40 characters, and a unit vector
// along this model's own axis with a little of the text in it, so a stored vector shows which model made it. `StopAt`
// cancels `Stop` on that call, as an engine stopping in the middle of a run does; `Refuses` fails a text the way a real
// model refuses one past its limit.
internal sealed class StandInModel(string id, int axis, int dimensions = 384) : IEmbedder
{
    public int Calls;
    public int StopAt;
    public CancellationTokenSource? Stop;
    public string? Refuses;
    public string Id => id;
    public float[] Embed(string text)
    {
        if (++Calls == StopAt) Stop?.Cancel();
        if (Refuses is not null && text.Contains(Refuses, StringComparison.Ordinal)) throw new ArgumentException("Embedding text exceeds model token limit");
        var v = new float[dimensions];
        v[axis] = 1;
        v[(axis + 1 + text.Length) % dimensions] += 0.25f;
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }
    public IEnumerable<string> Chunks(string content)
    {
        for (var i = 0; i < content.Length; i += 40) yield return content.Substring(i, Math.Min(40, content.Length - i));
    }
}

// Saved documents for the index job, as an AI client saves them while the local model is off and the backfill is on:
// asked to be indexed and stored without passages, marked for the index. Each is 200 characters, five passages with the
// stand-in's chunker, and holds the mark MARK<n> inside its second passage.
internal static class SavedDocuments
{
    // The model off, adapters.embeddings.backfill on.
    public static readonly Embeddings Pending = new() { Backfill = true };
    public static string Subject(int i) { var s = TestSettings.Use().Subjects; return s[i % s.Count]; }
    public static string Text(int i) => $"DEMO: fictional note {i:00} about the allotment rota, MARK{i:00}, the seed order and the shed key".PadRight(200, '.');

    // The saved rows' ids in save order, and the same ids in the database's own order (the order the job's cursor walks).
    public static async Task<(Guid Harness, Guid[] Saved, Guid[] Order)> Save(NpgsqlDataSource source, string prefix, int count)
    {
        TestSettings.Use();
        var token = Path.Combine(Path.GetTempPath(), "rhythm-index-" + Guid.NewGuid().ToString("N")[..10] + ".token");
        var harness = await new Principals(source).Create(prefix + "-harness", "harness", null, token, default);
        File.Delete(token);
        var saved = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var reply = await Store(source, harness, prefix, i, Text(i), DateTimeOffset.UtcNow.AddDays(-1));
            Assert.Equal(("embeddings-off", 0), (reply.GetProperty("semanticIndex").GetString(), reply.GetProperty("semanticChunks").GetInt32()));
            saved.Add(reply.GetProperty("resourceId").GetGuid());
        }
        await using var cmd = source.CreateCommand("SELECT id FROM resources WHERE id=ANY(@ids) ORDER BY id");
        cmd.Parameters.AddWithValue("ids", saved.ToArray());
        var order = new List<Guid>();
        await using (var row = await cmd.ExecuteReaderAsync()) while (await row.ReadAsync()) order.Add(row.GetGuid(0));
        return (harness, [.. saved], [.. order]);
    }

    public static async Task<JsonElement> Store(NpgsqlDataSource source, Guid harness, string prefix, int i, string text, DateTimeOffset modified) =>
        JsonDocument.Parse(await new DocumentStore(source, Pending).Save(harness,
            new DocumentSnapshot($"{prefix}-{i:00}", $"DEMO note {i}", null, text, modified, Subject: Subject(i)), default)).RootElement;

    // Every passage: its row, model, ordinal and the axis its vector points along.
    public static async Task<List<(Guid Row, string Model, int Ordinal, int Axis)>> Passages(NpgsqlDataSource source)
    {
        await using var cmd = source.CreateCommand("SELECT resource_id,model,ordinal,embedding::real[] FROM resource_chunks ORDER BY resource_id,model,ordinal");
        await using var row = await cmd.ExecuteReaderAsync();
        var all = new List<(Guid, string, int, int)>();
        while (await row.ReadAsync())
        {
            var vector = row.GetFieldValue<float[]>(3);
            all.Add((row.GetGuid(0), row.GetString(1), row.GetInt32(2), Array.IndexOf(vector, vector.Max())));
        }
        return all;
    }
}

// SB-P2-04: the index job resumes after a stop and never mixes models. One database for the whole story.
public sealed class EmbeddingBackfillTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_job_resumes_after_a_stop_and_never_mixes_models()
    {
        var (harness, saved, order) = await SavedDocuments.Save(fixture.Source, "resume", 6);
        // A document saved without a semantic index is never the job's.
        var plain = JsonDocument.Parse(await new DocumentStore(fixture.Source, Embeddings.Off).Save(harness, new DocumentSnapshot("resume-plain", "DEMO plain",
            null, "DEMO: a fictional note saved without an index.", DateTimeOffset.UtcNow.AddDays(-1), SemanticIndex: false), default)).RootElement;
        Assert.Equal("not-requested", plain.GetProperty("semanticIndex").GetString());
        // With the backfill off, every install's default, one asked to be indexed while the model is off is stored as
        // before: not marked for the index, so its metadata says what it holds, and the job never takes it.
        var unmarked = JsonDocument.Parse(await new DocumentStore(fixture.Source, Embeddings.Off).Save(harness, new DocumentSnapshot("resume-unmarked", "DEMO unmarked",
            null, "DEMO: a fictional note saved while the backfill is off.", DateTimeOffset.UtcNow.AddDays(-1)), default)).RootElement;
        Assert.Equal("embeddings-off", unmarked.GetProperty("semanticIndex").GetString());
        // Stored as the reply says: not marked, as the plain one; the six saved with the backfill on are marked.
        await using (var stored = fixture.Source.CreateCommand(
            "SELECT string_agg(coalesce(metadata->>'semanticIndex','-'),',' ORDER BY id=@u,id=@p) FROM resources WHERE id IN (@u,@p) OR id=ANY(@saved)"))
        {
            stored.Parameters.AddWithValue("u", unmarked.GetProperty("resourceId").GetGuid());
            stored.Parameters.AddWithValue("p", plain.GetProperty("resourceId").GetGuid());
            stored.Parameters.AddWithValue("saved", saved);
            Assert.Equal("true,true,true,true,true,true,false,false", await stored.ExecuteScalarAsync());
        }

        var a = new StandInModel("model-a", 0);
        var job = new EmbeddingBackfill(fixture.Db);
        // A bounded run: two rows, ten passages, the cursor after the second.
        var first = await job.Step(a, default, rows: 2);
        Assert.Equal((2, 0, 10, 4L, false), (first.Indexed, first.Failed, first.Passages, first.Left, first.Finished));
        var cursor = await job.Cursor("model-a", default);
        Assert.Equal(((Guid?)order[1], 1, 2), (cursor.After, cursor.Sweep, cursor.Indexed));

        // The engine stops half way through the third row: that row keeps nothing, and the cursor stays after the second.
        using (var stop = new CancellationTokenSource())
        {
            a.Stop = stop; a.StopAt = a.Calls + 2;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Step(a, stop.Token));
            a.Stop = null;
        }
        var passages = await SavedDocuments.Passages(fixture.Source);
        Assert.Equal([order[0], order[1]], passages.Select(p => p.Row).Distinct());
        Assert.Equal(order[1], (await job.Cursor("model-a", default)).After);

        // After the restart a new job resumes at the third row and embeds no finished row again.
        var calls = a.Calls;
        var resumed = new EmbeddingBackfill(fixture.Db);
        var rest = await resumed.Step(a, default);
        Assert.Equal((4, 0, 20, 0L), (rest.Indexed, rest.Failed, rest.Passages, rest.Left));
        Assert.Equal(calls + 20, a.Calls);
        // The sweep ends, and from then on a run finds nothing and embeds nothing: the job is idempotent, and so is a
        // direct Index of a row that has its passages.
        var end = await resumed.Step(a, default);
        Assert.Equal((true, 0L, 1), (end.Finished, end.Left, end.Sweep));
        var idle = await resumed.Step(a, default);
        Assert.Equal((true, 0, 0L, 2), (idle.Finished, idle.Indexed, idle.Left, idle.Sweep));
        var search = new SemanticSearch(fixture.Db, a);
        Assert.Equal(5, await search.Index(order[0], default));
        Assert.Null(await search.Index(plain.GetProperty("resourceId").GetGuid(), default));
        Assert.Equal(calls + 20, a.Calls);
        passages = await SavedDocuments.Passages(fixture.Source);
        Assert.Equal(order, passages.Select(p => p.Row).Distinct());
        Assert.All(order, id => Assert.Equal([0, 1, 2, 3, 4], passages.Where(p => p.Row == id).Select(p => p.Ordinal)));

        // A shadow model builds its own index beside model-a's, with its own cursor.
        var before = passages;
        var b = new StandInModel("model-b", 1);
        var shadow = await job.Step(b, default, rows: 3);
        Assert.Equal((3, 3L), (shadow.Indexed, shadow.Left));
        Assert.Equal(order[2], (await job.Cursor("model-b", default)).After);
        // The shadow changes to model-c half way: model-c starts a sweep of its own from the first row, and model-b's
        // cursor stays where it was, so model-b's index is only ever finished by model-b.
        var c = new StandInModel("model-c", 2);
        Assert.Equal(6, (await job.Step(c, default)).Indexed);
        Assert.Equal(order[2], (await job.Cursor("model-b", default)).After);
        Assert.Equal((3, 0L), ((await job.Step(b, default)).Indexed, (await job.Step(b, default)).Left));

        passages = await SavedDocuments.Passages(fixture.Source);
        // Every stored vector is the one its tag's model made, and model-a's passages are exactly as they were.
        var axis = new Dictionary<string, int> { ["model-a"] = 0, ["model-b"] = 1, ["model-c"] = 2 };
        Assert.All(passages, p => Assert.Equal(axis[p.Model], p.Axis));
        Assert.Equal(before, passages.Where(p => p.Model == "model-a"));
        Assert.All(axis.Keys, m => Assert.All(order, id => Assert.Equal(5, passages.Count(p => p.Row == id && p.Model == m))));

        // Search reads one model's passages: a query embedded by model-b is compared with model-b's only.
        foreach (var model in new[] { a, b })
        {
            var hits = JsonDocument.Parse(await new SemanticSearch(fixture.Db, model).Search(harness, "allotment", "", 20, default)).RootElement;
            Assert.Equal(20, hits.GetArrayLength());
            Assert.All(hits.EnumerateArray(), h => Assert.Equal(model.Id, h.GetProperty("model").GetString()));
        }

        // A document saved again with new text loses every model's passages, and model-a's next sweep gives it its own.
        await SavedDocuments.Store(fixture.Source, harness, "resume", 3, "DEMO: the rota changed, a fictional edit.".PadRight(200, '.'), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(await SavedDocuments.Passages(fixture.Source), p => p.Row == saved[3]);
        var again = await job.Step(a, default);
        Assert.Equal((1, 5, 0L), (again.Indexed, again.Passages, again.Left));
        Assert.Equal(["model-a"], (await SavedDocuments.Passages(fixture.Source)).Where(p => p.Row == saved[3]).Select(p => p.Model).Distinct());
    }
}

// The job's limits: a run stops at its budget but always takes its first row, a failing row never blocks the others, a
// sweep that only fails pauses that model's runs for an hour, and a vector of the wrong size is refused by the table.
public sealed class EmbeddingBackfillLimitTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [PostgresFact] public async Task A_failing_row_never_blocks_the_rest_and_a_sweep_that_only_fails_waits_an_hour()
    {
        var (_, saved, order) = await SavedDocuments.Save(fixture.Source, "limits", 4);
        var clock = new Clock();
        var job = new EmbeddingBackfill(fixture.Db, clock);
        // The second row in the cursor's order is one the model refuses.
        var refusing = new StandInModel("model-r", 3) { Refuses = $"MARK{Array.IndexOf(saved, order[1]):00}" };

        // The budget: one passage still runs the first row whole, and stops there.
        var first = await job.Step(refusing, default, passages: 1);
        Assert.Equal((1, 0, 5), (first.Indexed, first.Failed, first.Passages));
        // The refused row is counted, passed over and left pending; the rows after it are indexed.
        var rest = await job.Step(refusing, default);
        Assert.Equal((2, 1, 1L), (rest.Indexed, rest.Failed, rest.Left));
        Assert.True((await job.Step(refusing, default)).Finished);
        Assert.Equal(((Guid?)null, 2, (DateTimeOffset?)null), ((await job.Cursor("model-r", default)) is var c1 ? (c1.After, c1.Sweep, c1.PausedUntil) : default));
        // The next sweep only fails, so the model's runs pause for an hour; then the row is tried again.
        var retry = await job.Step(refusing, default);
        Assert.Equal((0, 1), (retry.Indexed, retry.Failed));
        Assert.True((await job.Step(refusing, default)).Finished);
        Assert.Equal(clock.Now + EmbeddingBackfill.Pause, (await job.Cursor("model-r", default)).PausedUntil);
        var calls = refusing.Calls;
        var waiting = await job.Step(refusing, default);
        Assert.Equal((true, 0, 1L), (waiting.Paused, waiting.Failed, waiting.Left));
        Assert.Equal(calls, refusing.Calls);
        clock.Now += EmbeddingBackfill.Pause + TimeSpan.FromMinutes(1);
        refusing.Refuses = null;
        var recovered = await job.Step(refusing, default);
        Assert.Equal((1, 0, 0L), (recovered.Indexed, recovered.Failed, recovered.Left));
        Assert.Null((await job.Cursor("model-r", default)).PausedUntil);

        // A model whose vectors are not the table's size fails every row and stores nothing.
        var wide = new StandInModel("model-wide", 0, 768);
        var refused = await job.Step(wide, default);
        Assert.Equal((0, 4, 4L), (refused.Indexed, refused.Failed, refused.Left));
        Assert.DoesNotContain(await SavedDocuments.Passages(fixture.Source), p => p.Model == "model-wide");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => job.Step(wide, default, rows: 0));
    }
}

// What the scheduler runs each minute: nothing with the settings an install has by default; the live model only with
// backfill on, the shadow model only when one is named and loaded, and never a model the settings did not name.
public sealed class EmbeddingBackfillTargetTests
{
    static RhythmConfig Settings(string embeddings) => RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings:\n" +
        string.Concat(embeddings.Split('\n').Select(l => "    " + l + "\n")));
    static string[] Roles(RhythmConfig config, IEmbedder? live, IEmbedder? shadow) =>
        [.. EmbeddingBackfill.Targets(EmbeddingSettings.From(config), live, shadow).Select(t => t.Role + ":" + t.Model.Id)];

    [Fact] public void Off_by_default_and_only_the_named_models_when_on()
    {
        var live = new StandInModel(EmbeddingModels.MiniLm.Id, 0);
        var shadow = new StandInModel(EmbeddingModels.BgeSmall.Id, 1);
        Assert.Empty(Roles(TestSettings.Use(), live, shadow));
        // A new install's settings list the texts the live model indexes (SB-P2-05, PassageTests); no backfill, no shadow.
        Assert.Equal(["live:" + live.Id], Roles(RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml")), live, shadow));
        Assert.Empty(Roles(Settings("enabled: true"), live, shadow));
        Assert.Equal(["live:" + live.Id], Roles(Settings("enabled: true\nbackfill: true"), live, shadow));
        Assert.Equal(["shadow:" + shadow.Id], Roles(Settings("enabled: true\nshadow: bge-small"), live, shadow));
        Assert.Equal(["live:" + live.Id, "shadow:" + shadow.Id], Roles(Settings("enabled: true\nbackfill: true\nshadow: bge-small"), live, shadow));
        // Not loaded yet, or turned off: nothing.
        Assert.Empty(Roles(Settings("enabled: true\nbackfill: true\nshadow: bge-small"), null, null));
        Assert.Empty(Roles(Settings("enabled: false\nbackfill: true\nshadow: bge-small"), live, shadow));
        // A loaded model that is not the one named never runs in its place.
        Assert.Empty(Roles(Settings("enabled: true\nbackfill: true\nshadow: bge-small"), shadow, live));
    }
}
