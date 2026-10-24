// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Metrics;

/// <summary>
/// The reducer-fidelity and reach-back metrics read an unmeasured query, or a missing metadata value, as not
/// measured: never as a lost fact, a missing penalty, or a halved score.
/// </summary>
public sealed class MemoryMetricBranchesNotMeasuredTests
{
    private static readonly MemoryFact CriticalFact = MemoryFact.Create("allergic to peanuts", "health", importance: 90);
    private static readonly MemoryFact FactA = MemoryFact.Create("lives in Madrid");
    private static readonly MemoryFact FactB = MemoryFact.Create("has a dog");

    [Fact]
    public async Task ReducerFidelity_WhenTheJudgeScoredNoQuery_IsNotMeasured_NotAFailAtZero()
    {
        // The judge failed, so MemoryJudge listed every expected fact as missing: a placeholder, not a loss.
        var memory = Result(
            [Query(measured: false, found: [], missing: [CriticalFact])],
            overallScore: 0);

        var result = await Reducer().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Contains("no score for any query", result.Explanation);
    }

    [Fact]
    public async Task ReducerFidelity_AnUnmeasuredQuerysFacts_AreNotCountedAsLost()
    {
        var memory = Result(
            [
                Query(measured: true, found: [FactA], missing: []),
                Query(measured: false, found: [], missing: [FactB]),
            ],
            overallScore: 90);

        var result = await Reducer().EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
        Assert.Equal(100, result.Score);
        Assert.Equal(0, result.Details!["facts_lost"]);
        Assert.Equal(1, result.Details["queries_not_measured"]);
        Assert.Contains("1 not measured", result.Explanation);
    }

    [Fact]
    public async Task ReducerFidelity_SpecificTest_WithoutTheLossMetadata_IsNotMeasured_NotANoPenaltyPass()
    {
        var memory = Result(
            [Query(measured: true, found: [FactA], missing: [])],
            overallScore: 90,
            metadata: new() { ["ReducerFidelityTest"] = true });

        var result = await Reducer().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Contains("InformationLoss", result.Explanation);
        Assert.Contains("KeyFactRetention", result.Explanation);
    }

    [Fact]
    public async Task ReducerFidelity_SpecificTest_WhenTheJudgeScoredNoQuery_IsNotMeasured()
    {
        var memory = Result(
            [Query(measured: false, found: [], missing: [FactA])],
            overallScore: 0,
            metadata: new()
            {
                ["ReducerFidelityTest"] = true,
                ["CompressionRatio"] = 1.0,
                ["InformationLoss"] = 10.0,
                ["KeyFactRetention"] = 95.0,
            });

        var result = await Reducer().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.Contains("no score for any query", result.Explanation);
    }

    [Fact]
    public async Task ReducerFidelity_SpecificTest_WithEveryValue_IsScored()
    {
        // Guard: the reducer branch still scores when it has what it reads (90 - 10 * 0.5 + 5 = 90).
        var memory = Result(
            [Query(measured: true, found: [FactA], missing: [])],
            overallScore: 90,
            metadata: new()
            {
                ["ReducerFidelityTest"] = true,
                ["CompressionRatio"] = 1.0,
                ["InformationLoss"] = 10.0,
                ["KeyFactRetention"] = 95.0,
            });

        var result = await Reducer().EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public async Task ReachBack_SpecificTest_WithoutMaxDepth_IsNotMeasured_NotAHalvedScore()
    {
        var memory = Result(
            [Query(measured: true, found: [FactA], missing: [])],
            overallScore: 90,
            metadata: new() { ["ReachBackTest"] = true });

        var result = await ReachBack().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Contains("MaxDepth", result.Explanation);
    }

    [Fact]
    public async Task ReachBack_SpecificTest_WhenTheJudgeScoredNoQuery_IsNotMeasured_NotAFailAtZero()
    {
        var memory = Result(
            [Query(measured: false, found: [], missing: [FactA])],
            overallScore: 0,
            metadata: new() { ["ReachBackTest"] = true, ["MaxDepth"] = 50 });

        var result = await ReachBack().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.Contains("no score for any query", result.Explanation);
    }

    [Fact]
    public async Task ReachBack_SpecificTest_WithMaxDepth_IsScored()
    {
        // Guard: 90 at a depth of 50 turns is 90.
        var memory = Result(
            [Query(measured: true, found: [FactA], missing: [])],
            overallScore: 90,
            metadata: new() { ["ReachBackTest"] = true, ["MaxDepth"] = 50 });

        var result = await ReachBack().EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
        Assert.True(result.Passed);
        Assert.Equal(90, result.Score);
    }

    private static MemoryReducerFidelityMetric Reducer() => new(NullLogger<MemoryReducerFidelityMetric>.Instance);

    private static MemoryReachBackMetric ReachBack() => new(NullLogger<MemoryReachBackMetric>.Instance);

    private static MemoryQueryResult Query(bool measured, MemoryFact[] found, MemoryFact[] missing) => new()
    {
        Query = MemoryQuery.Create("q?", [.. found, .. missing]),
        Response = "r",
        Score = measured ? 90 : 0,
        Measured = measured,
        FoundFacts = found,
        MissingFacts = missing,
        ForbiddenFound = [],
    };

    // The result-level fact lists are the unions over every query, as MemoryTestRunner builds them.
    private static MemoryEvaluationResult Result(
        MemoryQueryResult[] queries,
        double overallScore,
        Dictionary<string, object>? metadata = null) => new()
    {
        OverallScore = overallScore,
        QueryResults = queries,
        FoundFacts = queries.SelectMany(q => q.FoundFacts).Distinct().ToList(),
        MissingFacts = queries.SelectMany(q => q.MissingFacts).Distinct().ToList(),
        ForbiddenFound = [],
        Duration = TimeSpan.Zero,
        ScenarioName = "s",
        Metadata = metadata,
    };
}
