// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

public class GdprBenchmarkRunnerTests
{
    // ── Stub atomic evals with fixed scores ───────────────────────────────────

    private sealed class StubAtomicEval : AtomicEval
    {
        private readonly double _score;
        private readonly bool _passed;

        public StubAtomicEval(string key, double score, bool passed)
            : base(key, $"Stub-{key}", "compliance.test", "1.0.0")
        {
            _score = score;
            _passed = passed;
        }

        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(_score, null, _passed ? "pass" : "fail", _passed, 0.70, _passed ? "none" : "high", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-llm", "stub", null, null, null, 0.0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CompositeEval BuildTwoScenarioComposite(bool pass)
    {
        var components = new EvalComponent[]
        {
            new(new StubAtomicEval("scenario-a", pass ? 0.90 : 0.30, pass), Weight: 0.50),
            new(new StubAtomicEval("scenario-b", pass ? 0.85 : 0.25, pass), Weight: 0.50)
        };
        return new CompositeEval(
            key: "test.composite",
            name: "Test Composite",
            category: "compliance.test",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.70);
    }

    private static SubjectIdentity MakeSubject() =>
        new(SubjectKind.Agent, "test-agent", SourceProject: "test");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_TwoScenarioComposite_PersistsLeafScenarios_AndCompletesRun()
    {
        var store = new InMemoryOutputStore();
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: true);
        var subject = MakeSubject();
        var input = new EvalInput(Query: "test query", Response: "test response");

        var (runId, result) = await runner.RunAsync(store, subject, composite, input);

        // Composite result shape
        Assert.Equal("test.composite", result.Metric.Key);
        Assert.Equal(2, result.Details.SubResults!.Count);
        Assert.True(result.Score.Passed);

        // Verify 2 scenario results were persisted
        // We need to find the run by getting recent runs
        var runs = new List<RunPointer>();
        await foreach (var r in store.GetRecentRunsAsync(10))
            runs.Add(r);

        Assert.Single(runs);
        var runManifestFromStore = await store.GetRunManifestAsync(runs[0].RunId);
        Assert.NotNull(runManifestFromStore);

        // ContentHash should be non-trivial (not all zeros — CompleteRunAsync computes a real hash)
        Assert.NotNull(runManifestFromStore!.ContentHash);
        Assert.DoesNotContain("0000000000000000000000000000000000000000000000000000000000000000",  // DevSkim: ignore DS173237
            runManifestFromStore.ContentHash);

        // Verify 2 scenarios were written
        var scenarios = new List<ScenarioResult>();
        await foreach (var sr in store.GetScenarioResultsAsync(runs[0].RunId))
            scenarios.Add(sr);

        Assert.Equal(2, scenarios.Count);

        // Verify persistence round-trip: FromScenarioResult restores EvalResult
        foreach (var sr in scenarios)
        {
            var restored = EvalResultPersistence.FromScenarioResult(sr);
            Assert.NotNull(restored);
            Assert.Equal(sr.Score, restored!.Score.Value, precision: 10);
            Assert.Equal(sr.Passed, restored.Score.Passed);
        }

        // Run summary should be set
        var summary = await store.GetRunSummaryAsync(runs[0].RunId);
        Assert.NotNull(summary);
        Assert.Equal("PASS", summary!.Verdict);
        Assert.Equal(2, summary.Stats.Total);
        Assert.Equal(2, summary.Stats.Passed);
        Assert.Equal(0, summary.Stats.Failed);
        Assert.Contains("overallScore", summary.Metrics.Keys);
    }

    [Fact]
    public async Task RunAsync_FailingComposite_ProducesFailVerdictInSummary()
    {
        var store = new InMemoryOutputStore();
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: false);
        var subject = MakeSubject();
        var input = new EvalInput(Query: "test", Response: "bad response");

        await runner.RunAsync(store, subject, composite, input);

        var runs = new List<RunPointer>();
        await foreach (var r in store.GetRecentRunsAsync(10))
            runs.Add(r);

        var summary = await store.GetRunSummaryAsync(runs[0].RunId);
        Assert.NotNull(summary);
        Assert.Equal("FAIL", summary!.Verdict);

    }

    [Fact]
    public async Task RunAsync_NullStore_Throws()
    {
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: true);
        var input = new EvalInput(Query: "test", Response: "response");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(null!, MakeSubject(), composite, input));
    }

    [Fact]
    public async Task RunAsync_NullSubject_Throws()
    {
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: true);
        var input = new EvalInput(Query: "test", Response: "response");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(new InMemoryOutputStore(), null!, composite, input));
    }

    [Fact]
    public async Task RunAsync_NullBenchmark_Throws()
    {
        var runner = new GdprBenchmarkRunner();
        var input = new EvalInput(Query: "test", Response: "response");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(new InMemoryOutputStore(), MakeSubject(), null!, input));
    }

    [Fact]
    public async Task RunAsync_NullInput_Throws()
    {
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: true);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(new InMemoryOutputStore(), MakeSubject(), composite, null!));
    }

    [Fact]
    public async Task RunAsync_DefaultContext_IsUsedWhenNotProvided()
    {
        var store = new InMemoryOutputStore();
        var runner = new GdprBenchmarkRunner();
        var composite = BuildTwoScenarioComposite(pass: true);
        var input = new EvalInput(Query: "test", Response: "response");

        // No context passed — should use the default GdprBenchmarkRunner context
        await runner.RunAsync(store, MakeSubject(), composite, input, context: null);

        var runs = new List<RunPointer>();
        await foreach (var r in store.GetRecentRunsAsync(10))
            runs.Add(r);

        Assert.Single(runs);
        var manifest = await store.GetRunManifestAsync(runs[0].RunId);
        Assert.NotNull(manifest);
        Assert.Equal("GdprBenchmarkRunner", manifest!.Run.Harness);
        Assert.Equal("compliance", manifest.Run.Kind);
    }
}
