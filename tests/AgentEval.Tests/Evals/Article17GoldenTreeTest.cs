// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using Json.Schema;
using SchemaEvalOpts = Json.Schema.EvaluationOptions;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Executable spec for Phase 1 — mirrors the GDPR Article 17 worked example
/// from plan 02 §6 / §9.1. Builds a 4-component composite from fixed-score
/// stub evals and asserts the full result shape, including schema validation.
/// </summary>
public class Article17GoldenTreeTest
{
    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class Art17Acknowledgment()
        : AtomicEval("art17_acknowledgment", "Acknowledgment", "compliance.gdpr", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.95, null, "pass", true, null, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", "gpt-4o", "art17.v1", null, 100, 0.0015, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    private sealed class Art17BackupPropagation()
        : AtomicEval("art17_backup_propagation", "Backup Propagation", "compliance.gdpr", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.20, null, "fail", false, null, "high", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", "gpt-4o", "art17.v1", null, 100, 0.0015, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    private sealed class Art17LegalObligation()
        : AtomicEval("art17_legal_obligation", "Legal Obligation", "compliance.gdpr", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.85, null, "pass", true, null, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", "gpt-4o", "art17.v1", null, 100, 0.0015, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    private sealed class Art17NoOvererasure()
        : AtomicEval("art17_no_overerasure", "No Over-erasure", "compliance.gdpr", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.80, null, "pass", true, null, "low", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", "gpt-4o", "art17.v1", null, 100, 0.0015, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Schema helper ─────────────────────────────────────────────────────────

    private static JsonSchema LoadEvalResultSchema()
    {
        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eval-result.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string FormatErrors(EvaluationResults r) =>
        string.Join("\n", r.Details
            .Where(d => !d.IsValid)
            .Select(d => $"{d.EvaluationPath}: {string.Join(", ", d.Errors?.Select(e => $"{e.Key}={e.Value}") ?? [])}"));

    // ── JSON serialiser matching EvalResultPersistence ────────────────────────

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── Test ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GdprArticle17_GoldenTree_MatchesExpectedShape()
    {
        // Arrange — build the Article 17 composite from plan 02 §6
        var components = new EvalComponent[]
        {
            new(new Art17Acknowledgment(),    Weight: 0.30),
            new(new Art17BackupPropagation(), Weight: 0.30),
            new(new Art17LegalObligation(),   Weight: 0.20),
            new(new Art17NoOvererasure(),     Weight: 0.20)
        };

        var composite = new CompositeEval(
            key:         "gdpr_article_17",
            name:        "Article 17 - Right to erasure",
            category:    "compliance.gdpr",
            version:     "1.0.0",
            components:  components,
            aggregation: WeightedSumAggregation.Instance,
            threshold:   0.80);

        var input = new EvalInput(Query: "Please delete all my personal data.");

        // Act
        var result = await composite.EvaluateAsync(input);

        // ── Score assertions ──────────────────────────────────────────────────
        // Weighted sum: 0.95*0.30 + 0.20*0.30 + 0.85*0.20 + 0.80*0.20
        //             = 0.285 + 0.060 + 0.170 + 0.160 = 0.675
        const double expectedScore = 0.675;
        Assert.Equal(expectedScore, result.Score.Value, precision: 10);

        // Severity: backup_propagation is "high" — max wins
        Assert.Equal("high", result.Score.Severity);

        // Threshold / pass
        Assert.Equal(0.80, result.Score.Threshold);
        Assert.False(result.Score.Passed);   // 0.675 < 0.80
        Assert.Equal("fail", result.Score.Label);

        // ── Details assertions ────────────────────────────────────────────────
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(4, result.Details.SubResults!.Count);
        Assert.Equal("WeightedSum", result.Details.AggregationStrategy);

        // Sub-results appear in component order
        Assert.Equal("art17_acknowledgment",    result.Details.SubResults[0].Metric.Key);
        Assert.Equal("art17_backup_propagation", result.Details.SubResults[1].Metric.Key);
        Assert.Equal("art17_legal_obligation",  result.Details.SubResults[2].Metric.Key);
        Assert.Equal("art17_no_overerasure",    result.Details.SubResults[3].Metric.Key);

        // ── Provenance assertions ─────────────────────────────────────────────
        Assert.Equal("composite", result.Provenance.Type);

        // Cost rollup: 4 sub-evals × 0.0015 = 0.006
        const double expectedCost = 0.006;
        Assert.Equal(expectedCost, result.Provenance.EstimatedCost, precision: 10);

        // None of the stubs set cacheHit=true, so allCacheHits=false
        Assert.False(result.Provenance.CacheHit);

        // ── Schema validation ─────────────────────────────────────────────────
        var json = JsonSerializer.Serialize(result, s_jsonOpts);
        var schema = LoadEvalResultSchema();
        var schemaResult = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(schemaResult.IsValid, FormatErrors(schemaResult));
    }
}
