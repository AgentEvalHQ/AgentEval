// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for the <see cref="PerformanceBenchmark.EvaluateAsync"/> adapter that maps
/// latency/throughput/cost measurements to the unified <see cref="EvalResult"/> shape.
/// </summary>
public class PerformanceBenchmarkAdapterTests
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a PerformanceBenchmark with options tuned for fast deterministic tests:
    /// no verbosity, no delay, short throughput window, small iteration counts, and
    /// generous thresholds so a MockTestableAgent (synchronous) passes all metrics.
    /// </summary>
    private static PerformanceBenchmark FastBenchmark(
        IEvaluableAgent agent,
        PerformanceBenchmarkEvaluateOptions? evalOpts = null)
    {
        var opts = new PerformanceBenchmarkOptions
        {
            Verbose = false,
            DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = evalOpts ?? new PerformanceBenchmarkEvaluateOptions
            {
                P99LatencyThresholdMs = 60_000,         // generous — mock responds in <1ms
                LatencyIterationsPerPrompt = 2,
                WarmupIterations = 0,
                MinThroughputRps = 0.01,                // generous — mock is instant
                ThroughputConcurrentRequests = 1,
                ThroughputDuration = TimeSpan.FromMilliseconds(100),
                MaxCostUSD = 1.0,
                CompositePassThreshold = 0.6,
            }
        };
        return new PerformanceBenchmark(agent, opts);
    }

    // ─── Test 1: Happy path ───────────────────────────────────────────────────

    /// <summary>
    /// Happy-path end-to-end: a stub agent returns deterministic responses;
    /// EvaluateAsync should produce a CompositeEval-shaped EvalResult with three
    /// leaf sub-results (latency, throughput, cost), all passing against generous thresholds.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_HappyPath_ReturnsCompositeWithThreeLeaves()
    {
        // Arrange
        var agent = new MockTestableAgent("HappyAgent", "Success response");
        var benchmark = FastBenchmark(agent);
        var input = new EvalInput("Test prompt for performance measurement");

        // Act
        var result = await benchmark.EvaluateAsync(input);

        // Assert — top-level composite shape
        Assert.NotNull(result);
        Assert.Equal("perf_benchmark", result.Metric.Key);
        Assert.Equal("Performance Benchmark", result.Metric.Name);
        Assert.Equal("performance", result.Metric.Category);
        Assert.Equal("1.0.0", result.Metric.Version);
        Assert.Equal("CapByWorst", result.Details.AggregationStrategy);

        // Assert — three leaf sub-results exist
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(3, result.Details.SubResults.Count);

        var latencyLeaf    = result.Details.SubResults.Single(r => r.Metric.Key == "perf_latency");
        var throughputLeaf = result.Details.SubResults.Single(r => r.Metric.Key == "perf_throughput");
        var costLeaf       = result.Details.SubResults.Single(r => r.Metric.Key == "perf_cost");

        Assert.Equal("performance", latencyLeaf.Metric.Category);
        Assert.Equal("performance", throughputLeaf.Metric.Category);
        Assert.Equal("performance", costLeaf.Metric.Category);

        // Assert — leaf scores are in [0, 1]
        Assert.InRange(latencyLeaf.Score.Value,    0.0, 1.0);
        Assert.InRange(throughputLeaf.Score.Value, 0.0, 1.0);
        Assert.InRange(costLeaf.Score.Value,       0.0, 1.0);

        // Assert — leaf labels are valid
        Assert.Contains(latencyLeaf.Score.Label,    new[] { "pass", "warn", "fail", "skipped" });
        Assert.Contains(throughputLeaf.Score.Label, new[] { "pass", "warn", "fail", "skipped" });
        Assert.Contains(costLeaf.Score.Label,       new[] { "pass", "warn", "fail", "skipped" });

        // Assert — composite score is valid
        Assert.InRange(result.Score.Value, 0.0, 1.0);

        // Assert — latency leaf has expected dimensions
        Assert.NotNull(latencyLeaf.Details.Dimensions);
        Assert.True(latencyLeaf.Details.Dimensions.ContainsKey("p99_ms"), "Latency leaf must contain p99_ms dimension");
        Assert.True(latencyLeaf.Details.Dimensions.ContainsKey("p50_ms"), "Latency leaf must contain p50_ms dimension");

        // Assert — throughput leaf has rps dimension
        Assert.NotNull(throughputLeaf.Details.Dimensions);
        Assert.True(throughputLeaf.Details.Dimensions.ContainsKey("rps"), "Throughput leaf must contain rps dimension");

        // Assert — cost leaf has cost_usd dimension
        Assert.NotNull(costLeaf.Details.Dimensions);
        Assert.True(costLeaf.Details.Dimensions.ContainsKey("cost_usd"), "Cost leaf must contain cost_usd dimension");

        // Assert — top-level dimensions carry the key scalar measurements
        Assert.NotNull(result.Details.Dimensions);
        Assert.True(result.Details.Dimensions.ContainsKey("p99_ms"),   "Composite must lift p99_ms into dimensions");
        Assert.True(result.Details.Dimensions.ContainsKey("rps"),      "Composite must lift rps into dimensions");
        Assert.True(result.Details.Dimensions.ContainsKey("cost_usd"), "Composite must lift cost_usd into dimensions");

        // Assert — with generous thresholds a mock-instant agent should pass all leaves
        Assert.True(result.Score.Passed, "With generous thresholds the mock agent should pass the composite");
    }

    // ─── Test 2: Round-trip through IRunOutputStore ───────────────────────────

    /// <summary>
    /// Round-trip: persist the EvalResult via EvalResultPersistence.ToScenarioResult,
    /// write it to an in-memory IRunOutputStore, read it back, and verify the three leaf
    /// sub-results survive the serialise/deserialise cycle intact.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_RoundTripThroughOutputStore_PreservesSubResults()
    {
        // Arrange
        var agent = new MockTestableAgent("RoundTripAgent", "Hello round-trip");
        var benchmark = FastBenchmark(agent);
        var input = new EvalInput("Round-trip test prompt");

        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "RoundTripAgent");
        await store.EnsureSubjectAsync(subject);

        var ctx = new RunContext("AgentEval.Evals.Performance.Tests", "tests/", "xunit", null, null, "performance");
        var manifest = await store.StartRunAsync(subject, ctx);
        var runId = manifest.Run.RunId;

        // Act — evaluate and persist
        var evalResult = await benchmark.EvaluateAsync(input);
        var sr = EvalResultPersistence.ToScenarioResult(evalResult, "perf-001", "Performance Adapter Test");
        await store.WriteScenarioResultAsync(runId, sr);

        var summary = new RunSummary("1.0", runId, evalResult.Score.Passed ? "PASS" : "FAIL",
            new RunStats(1, evalResult.Score.Passed ? 1 : 0, evalResult.Score.Passed ? 0 : 1, 0),
            new Dictionary<string, double> { ["score"] = evalResult.Score.Value });
        await store.CompleteRunAsync(manifest, summary);

        // Read back
        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId))
            scenarios.Add(s);

        Assert.Single(scenarios);
        var restored = EvalResultPersistence.FromScenarioResult(scenarios[0]);

        // Assert — round-trip preserved the full tree
        Assert.NotNull(restored);

        // Compare by re-serialising (IReadOnlyList / IReadOnlyDictionary lack structural Equals).
        var original = JsonSerializer.Serialize(evalResult, s_jsonOpts);
        var roundTripped = JsonSerializer.Serialize(restored!, s_jsonOpts);
        Assert.Equal(original, roundTripped);

        // Assert — three sub-results survived the store write+read
        Assert.NotNull(restored!.Details.SubResults);
        Assert.Equal(3, restored.Details.SubResults.Count);

        var keys = restored.Details.SubResults.Select(r => r.Metric.Key).ToHashSet();
        Assert.Contains("perf_latency",    keys);
        Assert.Contains("perf_throughput", keys);
        Assert.Contains("perf_cost",       keys);

        // Assert — the manifest has a ContentHash (audit chain present)
        var updatedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(updatedManifest);
        Assert.Matches(@"^sha256:[a-f0-9]{64}$", updatedManifest!.ContentHash);
    }

    // ─── Test 3: Threshold violation → fail severity ─────────────────────────

    /// <summary>
    /// Threshold violation: configure a tight latency budget (1 ms P99 threshold)
    /// and a very low throughput minimum (1000 RPS); a synchronous mock agent can
    /// satisfy these individually depending on timing, but a DelayedAgent that
    /// adds real delay will definitely breach the latency budget.
    /// The EvalResult's latency leaf must be label=fail with severity high/critical.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_TightLatencyBudget_LatencyLeafFails()
    {
        // Arrange — agent with 50ms simulated latency
        var agent = new DelayedAgent("SlowAgent", delay: TimeSpan.FromMilliseconds(50), response: "Slow response");

        // Deliberately tight threshold: 1 ms — the 50ms agent will fail this
        var tightOpts = new PerformanceBenchmarkEvaluateOptions
        {
            P99LatencyThresholdMs = 1,               // 1 ms threshold — any real agent will breach
            LatencyIterationsPerPrompt = 2,
            WarmupIterations = 0,
            MinThroughputRps = 0.001,                // generous throughput — not what we're testing
            ThroughputConcurrentRequests = 1,
            ThroughputDuration = TimeSpan.FromMilliseconds(200),
            MaxCostUSD = 100.0,                      // generous cost — not what we're testing
            CompositePassThreshold = 0.6,
        };

        var benchmark = new PerformanceBenchmark(agent, new PerformanceBenchmarkOptions
        {
            Verbose = false,
            DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = tightOpts
        });

        var input = new EvalInput("Threshold violation test");

        // Act
        var result = await benchmark.EvaluateAsync(input);

        // Assert — latency leaf must have failed
        Assert.NotNull(result.Details.SubResults);
        var latencyLeaf = result.Details.SubResults.Single(r => r.Metric.Key == "perf_latency");

        Assert.Equal("fail", latencyLeaf.Score.Label);
        Assert.True(latencyLeaf.Score.Severity is "high" or "critical",
            $"Expected high or critical severity for a 50ms response vs 1ms threshold, got '{latencyLeaf.Score.Severity}'");
        Assert.False(latencyLeaf.Score.Passed, "Latency leaf must not pass when P99 > threshold");

        // Assert — composite should also fail (or warn at minimum) due to CapByWorst
        Assert.NotEqual("pass", result.Score.Label);

        // Assert — recommendations populated for the failing leaf
        Assert.NotNull(latencyLeaf.Details.Recommendations);
        Assert.True(latencyLeaf.Details.Recommendations.Count > 0,
            "Failing latency leaf must include at least one recommendation");
    }

    // ─── Test 4: Empty-input edge case ───────────────────────────────────────

    /// <summary>
    /// Empty-input guard: when EvalInput has no usable prompts (empty Query,
    /// no Metadata["prompts"]), EvaluateAsync must return a "skipped" result
    /// rather than throwing or producing silent nonsense.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_EmptyInput_ReturnsSkippedResult()
    {
        // Arrange
        var agent = new MockTestableAgent("SkipAgent", "Shouldn't be called");
        var benchmark = FastBenchmark(agent);

        // Empty Query, no Metadata
        var input = new EvalInput(Query: "");

        // Act
        var result = await benchmark.EvaluateAsync(input);

        // Assert — skipped result, not an exception, not nonsense
        Assert.NotNull(result);
        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed, "A skipped result must not be marked as passed");
        Assert.Equal(0.0, result.Score.Value);

        // The recommendations/details must explain why it was skipped
        Assert.NotNull(result.Details.Recommendations);
        Assert.True(result.Details.Recommendations.Count > 0,
            "Skipped result must include a reason in Recommendations");

        // Must NOT have sub-results (no measurements were run)
        Assert.True(
            result.Details.SubResults is null || result.Details.SubResults.Count == 0,
            "Skipped result must not contain sub-results — no measurements were taken");
    }

    // ─── Test 5: Prompts via Metadata["prompts"] ─────────────────────────────

    /// <summary>
    /// Verifies that prompts supplied via EvalInput.Metadata["prompts"] as a list
    /// are picked up correctly and drive a valid (non-skipped) evaluation.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_PromptsViaMetadata_RunsSuccessfully()
    {
        // Arrange
        var agent = new MockTestableAgent("MetadataAgent", "Metadata test response");
        var benchmark = FastBenchmark(agent);

        // Prompts supplied via Metadata instead of Query
        var prompts = new List<string> { "prompt one", "prompt two" };
        var input = new EvalInput(
            Query: "",
            Metadata: new Dictionary<string, object> { ["prompts"] = (IEnumerable<string>)prompts });

        // Act
        var result = await benchmark.EvaluateAsync(input);

        // Assert — should NOT be skipped; metadata prompts were consumed
        Assert.NotEqual("skipped", result.Score.Label);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(3, result.Details.SubResults.Count);
    }

    // ─── Test 6 (P0-1): cost-leaf actually consults the pricing table ────────

    /// <summary>
    /// Regression guard for P0-1 (Sprint 0). Before the fix, the cost-leaf was
    /// silently hard-coded to PASS because <c>EvaluateOptions.CostModelName</c>
    /// defaulted to null and the fallback to <c>_agent.Name</c> never matched
    /// the pricing table — so <c>EstimatedCostUSD</c> stayed null and
    /// the cost-leaf builder defaulted to score=1.0 / "pass".
    /// This test pins the new behaviour: when the operator
    /// supplies a real pricing-table key via <c>CostModelName</c>, the cost
    /// leaf MUST produce a real cost number (cost_usd > 0) and label it
    /// against the budget — proving the pricing-table lookup actually happened.
    /// </summary>
    [Fact]
    public async Task PerformanceBenchmark_CostLeaf_UsesCostModelName_FromOptions()
    {
        // Arrange — agent advertises token usage so the pricing lookup has
        // something to multiply against. "MyAgent" is deliberately NOT a
        // pricing-table key; without the fix, the cost leaf would fall back to
        // the agent name, miss the pricing table, and return null cost.
        var agent = new TokenReportingAgent(
            name: "MyAgent",
            response: "ok",
            inputTokens: 1000,
            outputTokens: 500);

        // The fix: caller sets CostModelName to a known pricing-table entry
        // (matches the pattern the CLI now uses with AZURE_OPENAI_DEPLOYMENT and
        // the Sample now uses with AIConfig.ModelDeployment).
        var optsWithCostModel = new PerformanceBenchmarkEvaluateOptions
        {
            P99LatencyThresholdMs = 60_000,
            LatencyIterationsPerPrompt = 1,
            WarmupIterations = 0,
            MinThroughputRps = 0.01,
            ThroughputConcurrentRequests = 1,
            ThroughputDuration = TimeSpan.FromMilliseconds(50),
            MaxCostUSD = 1.0,
            CompositePassThreshold = 0.6,
            CostModelName = "gpt-4o-mini",   // <-- the P0-1 fix
        };
        var benchmark = FastBenchmark(agent, optsWithCostModel);
        var input = new EvalInput("hi");

        // Act
        var result = await benchmark.EvaluateAsync(input);

        // Assert — cost leaf hit a real pricing entry, not the silent-PASS fallback
        Assert.NotNull(result.Details.SubResults);
        var costLeaf = result.Details.SubResults.Single(r => r.Metric.Key == "perf_cost");

        Assert.NotNull(costLeaf.Details.Dimensions);
        Assert.True(costLeaf.Details.Dimensions.ContainsKey("cost_usd"),
            "Cost leaf must surface cost_usd dimension");
        var costUsd = costLeaf.Details.Dimensions["cost_usd"];
        Assert.True(costUsd > 0,
            $"Expected cost_usd > 0 for gpt-4o-mini with 1000 in / 500 out tokens; got {costUsd}. " +
            "If this is 0, CostModelName was ignored and the leaf fell back to the silent-PASS branch.");

        // And — the cost leaf evidence (Reference field carries "agent (model)") must
        // mention the resolved model name we asked for.
        Assert.NotNull(costLeaf.Details.Evidence);
        Assert.Contains(costLeaf.Details.Evidence,
            e => e.Reference.Contains("gpt-4o-mini", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Companion to <see cref="PerformanceBenchmark_CostLeaf_UsesCostModelName_FromOptions"/>:
    /// when the caller does NOT pass <c>CostModelName</c> but the agent populates
    /// <see cref="AgentResponse.ModelId"/>, the new priority chain MUST still find
    /// the pricing entry via the response's reported model id (priority #2). This
    /// is the safety net that catches frameworks like MAF where deployment id is
    /// not known to the harness up front.
    /// </summary>
    [Fact]
    public async Task PerformanceBenchmark_CostLeaf_FallsBackTo_ResponseModelId_WhenOptionsMissing()
    {
        // Arrange — agent.Name is not a pricing key, but ModelId IS.
        var agent = new TokenReportingAgent(
            name: "MyAgent",
            response: "ok",
            inputTokens: 1000,
            outputTokens: 500,
            modelId: "gpt-4o-mini");

        // No CostModelName set — must fall back to response.ModelId.
        var opts = new PerformanceBenchmarkEvaluateOptions
        {
            P99LatencyThresholdMs = 60_000,
            LatencyIterationsPerPrompt = 1,
            WarmupIterations = 0,
            MinThroughputRps = 0.01,
            ThroughputConcurrentRequests = 1,
            ThroughputDuration = TimeSpan.FromMilliseconds(50),
            MaxCostUSD = 1.0,
            CompositePassThreshold = 0.6,
            CostModelName = null,
        };
        var benchmark = FastBenchmark(agent, opts);

        // Act
        var result = await benchmark.EvaluateAsync(new EvalInput("hi"));

        // Assert — response.ModelId rescued the lookup
        var costLeaf = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_cost");
        var costUsd = costLeaf.Details.Dimensions!["cost_usd"];
        Assert.True(costUsd > 0,
            $"Expected cost_usd > 0 via response.ModelId fallback; got {costUsd}.");
    }

    // ─── Private test helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Agent that adds a configurable delay to simulate slow responses.
    /// Used by the threshold-violation test.
    /// </summary>
    private sealed class DelayedAgent : IEvaluableAgent
    {
        private readonly TimeSpan _delay;
        private readonly string _response;

        public string Name { get; }

        public DelayedAgent(string name, TimeSpan delay, string response)
        {
            Name = name;
            _delay = delay;
            _response = response;
        }

        public async Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new AgentResponse { Text = _response };
        }
    }

    /// <summary>
    /// Agent that returns a fixed response with explicit token usage (and optionally
    /// a reported <see cref="AgentResponse.ModelId"/>). Used to exercise the cost-leaf
    /// pricing-table lookup in P0-1 regression tests.
    /// </summary>
    private sealed class TokenReportingAgent : IEvaluableAgent
    {
        private readonly string _response;
        private readonly int _inputTokens;
        private readonly int _outputTokens;
        private readonly string? _modelId;

        public string Name { get; }

        public TokenReportingAgent(
            string name,
            string response,
            int inputTokens,
            int outputTokens,
            string? modelId = null)
        {
            Name = name;
            _response = response;
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _modelId = modelId;
        }

        public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = _response,
                ModelId = _modelId,
                TokenUsage = new TokenUsage
                {
                    PromptTokens = _inputTokens,
                    CompletionTokens = _outputTokens,
                },
            });
        }
    }
}
