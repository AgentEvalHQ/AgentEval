// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Adjudication;
using Xunit;

namespace AgentEval.Tests.Agentic.Adjudication;

/// <summary>
/// Unit tests for <see cref="AdjudicatedMultiJudgeWrapper"/>.
/// </summary>
public class AdjudicatedMultiJudgeWrapperTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Stub IEval that always returns the given label/score.</summary>
    private sealed class StubEval(string label, double score = 1.0) : IEval
    {
        public string Key => $"stub_{label}";
        public string Name => $"Stub {label}";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(score, null, label, label == "pass", 0.70, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("stub", null, null, null, null, 0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    /// <summary>Adjudicator stub that throws if invoked.</summary>
    private sealed class ThrowingAdjudicator : IEval
    {
        public string Key => "throwing_adjudicator";
        public string Name => "Throwing Adjudicator";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            throw new InvalidOperationException("Adjudicator must NOT be invoked when panel agrees.");
    }

    /// <summary>Adjudicator stub that returns a fixed result and tracks invocation count.</summary>
    private sealed class TrackingAdjudicator : IEval
    {
        public int InvocationCount { get; private set; }

        public string Key => "tracking_adjudicator";
        public string Name => "Tracking Adjudicator";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            InvocationCount++;
            return Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(0.5, null, "warn", false, 0.70, "low", null),
                Details: new(null, null, new[] { "Adjudicator verdict: warn" }, null, null),
                Provenance: new("stub", null, null, null, null, 0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
        }
    }

    private static AdjudicatedMultiJudgeWrapper BuildWrapper(
        IReadOnlyList<EvalComponent> judges,
        IEval adjudicator,
        double threshold = 0.70)
        => new(
            key: "test.adjudicated",
            name: "Test Adjudicated",
            category: "test",
            version: "1.0.0",
            judges: judges,
            adjudicator: adjudicator,
            agreementThreshold: threshold);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_HasExpectedShape()
    {
        var judges = new EvalComponent[] { new(new StubEval("pass"), 1.0) };
        var wrapper = BuildWrapper(judges, new TrackingAdjudicator());

        Assert.Equal("test.adjudicated", wrapper.Key);
        Assert.Equal("Test Adjudicated", wrapper.Name);
        Assert.Equal("test", wrapper.Category);
        Assert.Equal("1.0.0", wrapper.Version);
    }

    [Fact]
    public async Task EvaluateAsync_HighAgreement_DoesNotInvokeAdjudicator()
    {
        // 3 judges all returning "pass" — should agree perfectly (kappa=1.0 or pairwise=1.0)
        var judges = new EvalComponent[]
        {
            new(new StubEval("pass", 1.0), 1.0),
            new(new StubEval("pass", 1.0), 1.0),
            new(new StubEval("pass", 1.0), 1.0),
        };
        var throwingAdjudicator = new ThrowingAdjudicator();
        var wrapper = BuildWrapper(judges, throwingAdjudicator, threshold: 0.70);

        var input = new EvalInput(Query: "test", Response: "response");
        // If adjudicator is invoked it throws — test passes only if it is NOT invoked
        var result = await wrapper.EvaluateAsync(input);

        Assert.NotNull(result);
        // Agreement should be high enough that we didn't adjudicate
        Assert.Equal(0.0, result.Details.Dimensions!["adjudicated"]);
        Assert.Equal(0.0, result.Details.Dimensions!["disputed"]);
        // Agreement value should be 1.0 (all agree on "pass")
        Assert.Equal(1.0, result.Details.Dimensions!["agreement"], precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_LowAgreement_InvokesAdjudicator()
    {
        // 3 judges with mixed verdicts: 1 pass, 1 fail, 1 warn — agreement should be low
        var judges = new EvalComponent[]
        {
            new(new StubEval("pass", 1.0), 1.0),
            new(new StubEval("fail", 0.0), 1.0),
            new(new StubEval("warn", 0.5), 1.0),
        };
        var trackingAdjudicator = new TrackingAdjudicator();
        // Set a high threshold so disagreement triggers adjudication
        var wrapper = BuildWrapper(judges, trackingAdjudicator, threshold: 0.90);

        var input = new EvalInput(Query: "test", Response: "mixed response");
        var result = await wrapper.EvaluateAsync(input);

        Assert.NotNull(result);
        // Adjudicator must have been invoked exactly once
        Assert.Equal(1, trackingAdjudicator.InvocationCount);
        // Metadata must reflect adjudication
        Assert.Equal(1.0, result.Details.Dimensions!["adjudicated"]);
        Assert.Equal(1.0, result.Details.Dimensions!["disputed"]);
        // AggregationStrategy must indicate adjudication
        Assert.Equal("Adjudicated", result.Details.AggregationStrategy);
        // SubResults should include panel (3) + adjudicator (1) = 4
        Assert.Equal(4, result.Details.SubResults!.Count);
    }
}
