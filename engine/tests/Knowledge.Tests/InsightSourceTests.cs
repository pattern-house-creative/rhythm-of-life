using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.Tests;

// SB-P5-07 (Q-076): one contract example per insight source kind (contracts/fixtures/app_insights[.<kind>].example.json),
// each within the app_insights schema and taken by save_insight's own check, so the app's reading of every kind is tested
// against what the engine accepts.
public sealed class InsightSourceTests
{
    static JsonNode Fixture(string kind) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts",
        kind == "graph" ? "app_insights.example.json" : $"app_insights.{kind}.example.json")))!;

    public static TheoryData<string> Kinds => [.. InsightDraft.SourceKinds.Order(StringComparer.Ordinal)];

    [Fact] public void The_schema_names_exactly_the_kinds_the_engine_takes()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(Schemas.Folder("mcp"), "app_insights.output.schema.json")))!;
        var named = schema["$defs"]!["insight"]!["properties"]!["sources"]!["items"]!["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(k => (string)k!).Order(StringComparer.Ordinal);
        Assert.Equal(InsightDraft.SourceKinds.Order(StringComparer.Ordinal), named);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_example_insight_of_each_source_kind_is_one_save_insight_takes(string kind)
    {
        var fixture = Fixture(kind);
        Schemas.Valid(Schemas.Folder("mcp"), "app_insights.output.schema.json", fixture);
        var insights = fixture["insights"]!.AsArray();
        Assert.NotEmpty(insights);
        foreach (var insight in insights)
        {
            var draft = insight.Deserialize<InsightDraft>(InsightDraft.Json)!;
            Assert.Contains(draft.Sources, s => s.Kind == kind);
            Assert.Null(draft.Problem(DateTimeOffset.Parse("2026-10-28T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
