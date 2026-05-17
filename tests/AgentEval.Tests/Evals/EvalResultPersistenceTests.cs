// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals;

public class EvalResultPersistenceTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static EvalResult MakeAtomicResult() =>
        new(
            Metric: new("art17_acknowledgment", "Acknowledgment", "compliance.gdpr", "1.0.0"),
            Score: new(0.95, null, "pass", true, null, "none", 0.92),
            Details: new(
                Dimensions: new Dictionary<string, double> { ["clarity"] = 0.95 },
                Evidence: new[] { new EvalEvidence("response", "We confirm receipt of your request.", "Acknowledgment is explicit.") },
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-llm", "gpt-4o", "ack.v1", null, 142, 0.0015, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:30:00Z"));

    private static EvalResult MakeCompositeResult()
    {
        var child1 = new EvalResult(
            Metric: new("sub_a", "Sub A", "quality", "1.0.0"),
            Score: new(0.8, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0.001, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:30:00Z"));

        var child2 = new EvalResult(
            Metric: new("sub_b", "Sub B", "quality", "1.0.0"),
            Score: new(0.6, null, "fail", false, null, "medium", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0.001, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:30:01Z"));

        return new(
            Metric: new("composite_quality", "Composite Quality", "quality", "1.0.0"),
            Score: new(0.7, null, "warn", false, 0.75, "medium", null),
            Details: new(
                Dimensions: new Dictionary<string, double> { ["score_a"] = 0.8, ["score_b"] = 0.6 },
                Evidence: null,
                Recommendations: new[] { "Improve sub_b." },
                SubResults: new[] { child1, child2 },
                AggregationStrategy: "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0.002, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:30:02Z"));
    }

    private static EvalResult MakeDeepComposite()
    {
        // Level 3 (innermost)
        var leaf = new EvalResult(
            Metric: new("leaf", "Leaf", "q", "1.0.0"),
            Score: new(0.9, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:00:00Z"));

        // Level 2
        var mid = new EvalResult(
            Metric: new("mid", "Mid", "q", "1.0.0"),
            Score: new(0.85, null, "pass", true, null, "none", null),
            Details: new(null, null, null, new[] { leaf }, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:00:01Z"));

        // Level 1 (root)
        return new(
            Metric: new("root", "Root", "q", "1.0.0"),
            Score: new(0.82, null, "pass", true, null, "none", null),
            Details: new(null, null, null, new[] { mid }, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:00:02Z"));
    }

    private static string Serialize(EvalResult r) =>
        JsonSerializer.Serialize(r, s_jsonOpts);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ToScenarioResult_ThenFromScenarioResult_RoundTripPreservesStructure()
    {
        var original = MakeAtomicResult();
        var sr = EvalResultPersistence.ToScenarioResult(original, "scen-001", "Acknowledgment Scenario");
        var restored = EvalResultPersistence.FromScenarioResult(sr);

        Assert.NotNull(restored);
        // Compare by re-serializing — IReadOnlyDictionary / IReadOnlyList have no structural Equals.
        Assert.Equal(Serialize(original), Serialize(restored!));
    }

    [Fact]
    public void ToScenarioResult_ThenFromScenarioResult_RecursiveCompositeRoundTrip()
    {
        var deep = MakeDeepComposite();
        var sr = EvalResultPersistence.ToScenarioResult(deep, "scen-deep", "Deep Composite");
        var restored = EvalResultPersistence.FromScenarioResult(sr);

        Assert.NotNull(restored);
        Assert.Equal(Serialize(deep), Serialize(restored!));

        // Spot-check the 3-level nesting survived.
        var midRestored = restored!.Details.SubResults![0];
        var leafRestored = midRestored.Details.SubResults![0];
        Assert.Equal("leaf", leafRestored.Metric.Key);
        Assert.Equal(0.9, leafRestored.Score.Value, precision: 10);
    }

    [Fact]
    public async Task EndToEnd_AuditChain_InMemoryStore_RoundTrips()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "EvalAgent");
        await store.EnsureSubjectAsync(subject);

        var ctx = new RunContext("Evals.Test", "path", "xunit", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, ctx);
        var runId = manifest.Run.RunId;

        var result1 = MakeAtomicResult();
        var result2 = MakeCompositeResult();

        var sr1 = EvalResultPersistence.ToScenarioResult(result1, "scen-001", "Scenario One");
        var sr2 = EvalResultPersistence.ToScenarioResult(result2, "scen-002", "Scenario Two");

        await store.WriteScenarioResultAsync(runId, sr1);
        await store.WriteScenarioResultAsync(runId, sr2);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(2, 1, 1, 0),
            new Dictionary<string, double> { ["score"] = 0.82 });
        await store.CompleteRunAsync(manifest, summary);

        // Read back all scenarios and restore EvalResults.
        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId))
            scenarios.Add(s);

        Assert.Equal(2, scenarios.Count);

        var r1Restored = EvalResultPersistence.FromScenarioResult(scenarios[0]);
        var r2Restored = EvalResultPersistence.FromScenarioResult(scenarios[1]);

        Assert.NotNull(r1Restored);
        Assert.NotNull(r2Restored);
        Assert.Equal(Serialize(result1), Serialize(r1Restored!));
        Assert.Equal(Serialize(result2), Serialize(r2Restored!));

        // Verify the manifest has a populated ContentHash (audit chain confirmed).
        var updatedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(updatedManifest);
        Assert.Matches(@"^sha256:[a-f0-9]{64}$", updatedManifest!.ContentHash);
    }

    [Fact]
    public void FromScenarioResult_EmptyOutput_ReturnsNull()
    {
        var sr = new ScenarioResult(
            "id", "name", "", "",
            false, 0.0,
            new Dictionary<string, double>(),
            Array.Empty<AssertionResult>(),
            TimeSpan.Zero, 0.0);

        var result = EvalResultPersistence.FromScenarioResult(sr);

        Assert.Null(result);
    }

    [Fact]
    public void FromScenarioResult_InvalidJson_ReturnsNull()
    {
        var sr = new ScenarioResult(
            "id", "name", "", "not json",
            false, 0.0,
            new Dictionary<string, double>(),
            Array.Empty<AssertionResult>(),
            TimeSpan.Zero, 0.0);

        var result = EvalResultPersistence.FromScenarioResult(sr);

        Assert.Null(result);
    }

    [Fact]
    public void ToScenarioResult_ArgumentValidation_ThrowsOnNullOrEmpty()
    {
        var validResult = MakeAtomicResult();

        // null result
        Assert.Throws<ArgumentNullException>(() =>
            EvalResultPersistence.ToScenarioResult(null!, "id", "name"));

        // empty scenarioId
        Assert.Throws<ArgumentException>(() =>
            EvalResultPersistence.ToScenarioResult(validResult, "", "name"));

        // whitespace scenarioId
        Assert.Throws<ArgumentException>(() =>
            EvalResultPersistence.ToScenarioResult(validResult, "   ", "name"));

        // empty scenarioName
        Assert.Throws<ArgumentException>(() =>
            EvalResultPersistence.ToScenarioResult(validResult, "id", ""));

        // whitespace scenarioName
        Assert.Throws<ArgumentException>(() =>
            EvalResultPersistence.ToScenarioResult(validResult, "id", "   "));
    }

    // ── Metrics-lift (batch-4 surface) ───────────────────────────────────────

    [Theory]
    [InlineData("none",     0)]
    [InlineData("low",      1)]
    [InlineData("medium",   2)]
    [InlineData("high",     3)]
    [InlineData("critical", 4)]
    public void ToScenarioResult_SeverityOrdinalLiftedIntoMetrics(string severity, double expectedOrdinal)
    {
        // Arrange — atomic result with the given severity
        var result = new EvalResult(
            Metric: new("test_metric", "Test", "test", "1.0.0"),
            Score: new(0.5, null, severity == "none" ? "pass" : "fail", severity == "none", null, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        // Act
        var sr = EvalResultPersistence.ToScenarioResult(result, "scenario-1", "Scenario 1");

        // Assert — _lifted.severity_ordinal lifted so callers can query Metrics
        // without re-deserialising the recursive Output JSON. Note the
        // `_lifted.` prefix added in pass-3 to avoid collisions with consumer
        // dimensions that legitimately use "severity_ordinal" as a key.
        Assert.True(sr.Metrics.ContainsKey("_lifted.severity_ordinal"),
            "_lifted.severity_ordinal must be lifted into Metrics for query-side use");
        Assert.Equal(expectedOrdinal, sr.Metrics["_lifted.severity_ordinal"]);
    }

    [Fact]
    public void ToScenarioResult_ConfidenceLiftedIntoMetrics_WhenPresent()
    {
        var result = new EvalResult(
            Metric: new("test_metric", "Test", "test", "1.0.0"),
            Score: new(0.9, null, "pass", true, null, "none", Confidence: 0.92),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var sr = EvalResultPersistence.ToScenarioResult(result, "scenario-1", "Scenario 1");

        Assert.True(sr.Metrics.ContainsKey("_lifted.confidence"));
        Assert.Equal(0.92, sr.Metrics["_lifted.confidence"]);
    }

    [Fact]
    public void ToScenarioResult_ConfidenceAbsentFromMetrics_WhenNull()
    {
        var result = new EvalResult(
            Metric: new("test_metric", "Test", "test", "1.0.0"),
            Score: new(0.9, null, "pass", true, null, "none", Confidence: null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var sr = EvalResultPersistence.ToScenarioResult(result, "scenario-1", "Scenario 1");

        Assert.False(sr.Metrics.ContainsKey("_lifted.confidence"));
    }

    [Fact]
    public void ToScenarioResult_DimensionsArePreservedAlongsideLiftedMetrics()
    {
        var dimensions = new Dictionary<string, double>
        {
            ["clarity"] = 0.95,
            ["accuracy"] = 0.80,
        };
        var result = new EvalResult(
            Metric: new("test_metric", "Test", "test", "1.0.0"),
            Score: new(0.9, null, "pass", true, null, "low", 0.85),
            Details: new(dimensions, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var sr = EvalResultPersistence.ToScenarioResult(result, "scenario-1", "Scenario 1");

        // Dimensions survive
        Assert.Equal(0.95, sr.Metrics["clarity"]);
        Assert.Equal(0.80, sr.Metrics["accuracy"]);
        // Lifted alongside under `_lifted.*` keys
        Assert.Equal(1.0,  sr.Metrics["_lifted.severity_ordinal"]); // low=1
        Assert.Equal(0.85, sr.Metrics["_lifted.confidence"]);
    }

    /// <summary>
    /// Phase 3 / Task 3.6 regression — namespacing prevents lifted metrics
    /// from clobbering consumer-supplied Dimensions whose key happens to
    /// coincide with one of the lifted names. Without the `_lifted.` prefix
    /// added in pass-3, the dimension was silently overwritten.
    /// </summary>
    [Fact]
    public void ToScenarioResult_DimensionsNamed_Confidence_OrSeverityOrdinal_AreNOTOverwritten()
    {
        var dimensions = new Dictionary<string, double>
        {
            // Plausible domain words a consumer might use as criterion names.
            ["confidence"]       = 0.42,
            ["severity_ordinal"] = 99.9,
        };
        var result = new EvalResult(
            Metric: new("test_metric", "Test", "test", "1.0.0"),
            Score: new(0.9, null, "fail", false, null, "high", Confidence: 0.92),
            Details: new(dimensions, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var sr = EvalResultPersistence.ToScenarioResult(result, "scenario-1", "Scenario 1");

        // Consumer dimensions preserved
        Assert.Equal(0.42, sr.Metrics["confidence"]);
        Assert.Equal(99.9, sr.Metrics["severity_ordinal"]);
        // Lifted values under namespaced keys
        Assert.Equal(0.92, sr.Metrics["_lifted.confidence"]);
        Assert.Equal(3.0,  sr.Metrics["_lifted.severity_ordinal"]);  // high = 3
    }

    // ── Phase-9 / Task 7.19 closure: deep-tree persistence regression

    /// <summary>
    /// Producer-side guard (Phase-7 Task 7.5) caps composite trees at depth=32,
    /// but if a hostile / malformed tree is constructed directly (bypassing
    /// CompositeEval) and passed through persistence, the underlying
    /// System.Text.Json default MaxDepth=64 will throw or fail rather than
    /// silently truncating. This test pins the defence-in-depth behaviour: a
    /// 70-deep tree must either round-trip cleanly (if STJ tolerates it) or
    /// throw a deterministic exception — it must NOT silently lose data or
    /// produce a partial result.
    /// </summary>
    [Fact]
    public void ToFromScenarioResult_DeepTree_BeyondStjDefaultMaxDepth_GracefullyHandled()
    {
        // Build a 70-level chain manually (bypasses CompositeEval producer guard).
        EvalResult Leaf() => new(
            Metric: new("leaf", "Leaf", "test", "1.0.0"),
            Score: new(0.5, null, "warn", false, null, "medium", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0.001, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        EvalResult Build(int level)
        {
            if (level == 0) return Leaf();
            return new EvalResult(
                Metric: new($"level-{level}", $"Level{level}", "composite", "1.0.0"),
                Score: new(0.5, null, "warn", false, null, "medium", null),
                Details: new(null, null, null, new[] { Build(level - 1) }, "WeightedSum"),
                Provenance: new("composite", null, null, null, null, 0.001, false),
                EvaluatedAt: DateTimeOffset.UtcNow);
        }

        var deepTree = Build(70);

        // ToScenarioResult writes the tree as embedded JSON. With STJ default
        // MaxDepth=64 and a depth-70 tree, the serializer should either
        // (a) succeed (if STJ tolerates this depth) or
        // (b) throw a deterministic JsonException — both are acceptable.
        // What is NOT acceptable: silent data loss or a corrupted ScenarioResult.
        try
        {
            var sr = EvalResultPersistence.ToScenarioResult(deepTree, "deep-tree", "Deep tree");
            // If serialization succeeded, the round-trip via FromScenarioResult
            // must produce an EvalResult that's either null (graceful failure)
            // or structurally consistent (no partial / silently-truncated tree).
            var round = EvalResultPersistence.FromScenarioResult(sr);
            if (round is not null)
            {
                // Walk down and count actual depth — must match what we serialised
                // (no silent truncation).
                int actualDepth = 0;
                var current = round;
                while (current.Details.SubResults is { Count: > 0 } subs)
                {
                    current = subs[0];
                    actualDepth++;
                    if (actualDepth > 200) break; // safety
                }
                Assert.Equal(70, actualDepth);
            }
            // If round is null, that's an acceptable graceful failure.
        }
        catch (System.Text.Json.JsonException)
        {
            // Acceptable deterministic failure mode — depth limit enforced.
        }
    }
}
