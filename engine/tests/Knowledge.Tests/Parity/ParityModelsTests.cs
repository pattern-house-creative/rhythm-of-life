namespace Knowledge.Tests.Parity;

// Which embedding model a parity run loads (ParityModelChoice, SB-P2-06): MiniLM from RHYTHM_MODEL_DIR unless told
// otherwise, and a question instruction that changes the question alone. No model files needed.
public sealed class ParityModelsTests
{
    sealed class Echo : IEmbedder
    {
        public string Id => "echo";
        public List<string> Asked { get; } = [];
        public float[] Embed(string text) { Asked.Add(text); return [1f]; }
        public IEnumerable<string> Chunks(string content) => [content];
    }

    [Fact] public void A_query_instruction_changes_the_question_only_and_keeps_the_models_id()
    {
        var model = new Echo();
        var asker = new QueryInstruction(model, QueryInstruction.Bge);
        Assert.Equal("echo", asker.Id);
        asker.Embed("trains or flights?");
        Assert.Equal(["Represent this sentence for searching relevant passages: trains or flights?"], model.Asked);
        Assert.Equal(["a passage as it is"], asker.Chunks("a passage as it is"));
        Assert.Equal(QueryInstruction.Bge, QueryInstruction.For(EmbeddingModels.BgeSmall));
        Assert.Null(QueryInstruction.For(EmbeddingModels.MiniLm));
    }

    [Fact] public void A_parity_run_loads_minilm_unless_told_otherwise()
    {
        var env = new Dictionary<string, string?> { ["RHYTHM_MODEL_DIR"] = "mini", ["RHYTHM_MODEL_DIR_BGE_SMALL"] = "bge" };
        ParityModelChoice Choice() => ParityModelChoice.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(new ParityModelChoice(EmbeddingModels.MiniLm, "mini", null), Choice());
        Assert.Equal(EmbeddingModels.MiniLmId, Choice().Label);
        env["RHYTHM_PARITY_MODEL"] = "bge-small";
        Assert.Equal(new ParityModelChoice(EmbeddingModels.BgeSmall, "bge", null), Choice());
        env["RHYTHM_PARITY_QUERY_INSTRUCTION"] = "on";
        Assert.Equal(new ParityModelChoice(EmbeddingModels.BgeSmall, "bge", QueryInstruction.Bge), Choice());
        env["RHYTHM_PARITY_MODEL"] = "minilm";
        Assert.Throws<ArgumentException>(Choice);
        // bge-base (D-253) from its own folder, the same instruction as bge-small's; an unknown model is refused.
        env["RHYTHM_PARITY_MODEL"] = "bge-base";
        env["RHYTHM_MODEL_DIR_BGE_BASE"] = "base";
        Assert.Equal(new ParityModelChoice(EmbeddingModels.BgeBase, "base", QueryInstruction.Bge), Choice());
        env["RHYTHM_PARITY_MODEL"] = "bge-large";
        Assert.Throws<ArgumentException>(Choice);
        env["RHYTHM_PARITY_MODEL"] = "bge-small";
        env["RHYTHM_PARITY_QUERY_INSTRUCTION"] = "yes";
        Assert.Throws<ArgumentException>(Choice);
    }

    // A gate run writes a locked baseline only for what an install runs: MiniLM with questions as they are. Another model
    // (D-025) or the query instruction (D-068) is refused, saying why, so no lock holds answers no install gives.
    [Fact] public void Only_the_live_model_with_questions_as_they_are_may_be_locked()
    {
        Assert.Null(ParityModelChoice.Default(_ => "mini").LockRefusal());
        Assert.Contains("(D-025)", new ParityModelChoice(EmbeddingModels.BgeSmall, "bge", null).LockRefusal());
        Assert.Contains("(D-068)", new ParityModelChoice(EmbeddingModels.BgeSmall, "bge", QueryInstruction.Bge).LockRefusal());
        Assert.Contains("(D-256)", (ParityModelChoice.Default(_ => "mini") with { Map = EmbeddingModels.BgeSmall }).LockRefusal());
    }

    // RHYTHM_PARITY_MAP_MODEL (D-256): a map model beside the live one, from its own folder; the label the locks compare
    // stays the live model's. Only what graph.similarity.model takes.
    [Fact] public void A_parity_run_loads_a_map_model_only_when_told_and_keeps_the_live_models_label()
    {
        var env = new Dictionary<string, string?> { ["RHYTHM_MODEL_DIR"] = "mini", ["RHYTHM_MODEL_DIR_BGE_SMALL"] = "bge" };
        ParityModelChoice Choice() => ParityModelChoice.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(((EmbeddingModel?)null, (string?)null), (Choice().Map, Choice().MapDirectory));
        env["RHYTHM_PARITY_MAP_MODEL"] = "bge-small";
        Assert.Equal((EmbeddingModels.MiniLm, (EmbeddingModel?)EmbeddingModels.BgeSmall, "bge", EmbeddingModels.MiniLmId),
            (Choice().Spec, Choice().Map, Choice().MapDirectory, Choice().Label));
        foreach (var wrong in new[] { "bge-base", "minilm" })
        {
            env["RHYTHM_PARITY_MAP_MODEL"] = wrong;
            Assert.Throws<ArgumentException>(Choice);
        }
    }

    // RHYTHM_PARITY_MAP_MAXLINKS (D-258): the cap graph.similarity.maxLinks takes, with or without a map model; a run with
    // it never writes a lock.
    [Fact] public void A_parity_run_caps_the_maps_similar_links_only_when_told_and_then_writes_no_lock()
    {
        var env = new Dictionary<string, string?> { ["RHYTHM_MODEL_DIR"] = "mini", ["RHYTHM_MODEL_DIR_BGE_SMALL"] = "bge" };
        ParityModelChoice Choice() => ParityModelChoice.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(((int?)null, (string?)null), (Choice().MaxLinks, Choice().LockRefusal()));
        env["RHYTHM_PARITY_MAP_MAXLINKS"] = "6";
        Assert.Equal((6, (EmbeddingModel?)null, EmbeddingModels.MiniLmId), (Choice().MaxLinks, Choice().Map, Choice().Label));
        Assert.Contains("graph.similarity.maxLinks", Choice().LockRefusal());
        env["RHYTHM_PARITY_MAP_MODEL"] = "bge-small";
        Assert.Equal((6, (EmbeddingModel?)EmbeddingModels.BgeSmall), (Choice().MaxLinks, Choice().Map));
        foreach (var wrong in new[] { "1", "51", "six", "6.5", "-6" })
        {
            env["RHYTHM_PARITY_MAP_MAXLINKS"] = wrong;
            Assert.Throws<ArgumentException>(Choice);
        }
    }
}
