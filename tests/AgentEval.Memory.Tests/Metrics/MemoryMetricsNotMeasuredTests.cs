// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Metrics;

/// <summary>
/// A memory metric with nothing to read, or nothing the judge scored, is not measured: neither a fail at 0 nor a pass.
/// </summary>
public sealed class MemoryMetricsNotMeasuredTests
{
    public static TheoryData<string> AllMetrics =>
        ["retention", "reach_back", "noise_resilience", "temporal", "reducer_fidelity"];

    private static IMetric Create(string name) => name switch
    {
        "retention" => new MemoryRetentionMetric(NullLogger<MemoryRetentionMetric>.Instance),
        "reach_back" => new MemoryReachBackMetric(NullLogger<MemoryReachBackMetric>.Instance),
        "noise_resilience" => new MemoryNoiseResilienceMetric(NullLogger<MemoryNoiseResilienceMetric>.Instance),
        "temporal" => new MemoryTemporalMetric(NullLogger<MemoryTemporalMetric>.Instance),
        "reducer_fidelity" => new MemoryReducerFidelityMetric(NullLogger<MemoryReducerFidelityMetric>.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(AllMetrics))]
    public async Task WithoutAMemoryResult_IsNotMeasured(string metric)
    {
        var result = await Create(metric).EvaluateAsync(new EvaluationContext { Input = "q", Output = "a" });

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Contains("not found", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("reach_back")]
    [InlineData("noise_resilience")]
    [InlineData("temporal")]
    public async Task WhenTheJudgeScoredNoQuery_IsNotMeasured_NotAFailAtZero(string metric)
    {
        var memory = Result([Query(measured: false), Query(measured: false)],
            temporal: metric == "temporal");

        var result = await Create(metric).EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.Contains("no score for any query", result.Explanation);
    }

    [Fact]
    public async Task ReachBack_WithNoQueries_IsNotMeasured_NotAPass()
    {
        var result = await Create("reach_back").EvaluateAsync(Result([]).ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ReducerFidelity_WithNoExpectedFacts_IsNotMeasured_NotAPassAt100()
    {
        var result = await Create("reducer_fidelity").EvaluateAsync(Result([Query(measured: true)]).ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("reach_back")]
    [InlineData("temporal")]
    public async Task OneScoredQuery_IsEnoughToMeasure(string metric)
    {
        var memory = Result([Query(measured: true), Query(measured: false)], temporal: metric == "temporal");

        var result = await Create(metric).EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
    }

    [Fact]
    public async Task ReachBack_ReportsTheMeasuredDenominator_AndTheUnmeasuredApart()
    {
        // One measured pass, two unmeasured: 1/1 at 100 %, never "1/3 (100%)".
        var memory = Result([Query(measured: true), Query(measured: false), Query(measured: false)]);

        var result = await Create("reach_back").EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(memory.QueryResults.First().Passed);
        Assert.Equal(100, result.Score);
        Assert.Contains("1/1 measured queries", result.Explanation);
        Assert.Contains("2 not measured", result.Explanation);
        Assert.Equal(1, result.Details!["queries_analyzed"]);
        Assert.Equal(2, result.Details["queries_not_measured"]);
    }

    private static MemoryQueryResult Query(bool measured) => new()
    {
        Query = MemoryQuery.Create("q?", MemoryFact.Create("fact")),
        Response = "r",
        Score = measured ? 90 : 0,
        Measured = measured,
        FoundFacts = [],
        MissingFacts = [],
        ForbiddenFound = [],
    };

    private static MemoryEvaluationResult Result(MemoryQueryResult[] queries, bool temporal = false) => new()
    {
        OverallScore = queries.Any(q => q.Measured) ? 90 : 0,
        QueryResults = queries,
        FoundFacts = [],
        MissingFacts = [],
        ForbiddenFound = [],
        Duration = TimeSpan.Zero,
        ScenarioName = "s",
        Metadata = temporal ? new Dictionary<string, object> { ["TemporalEvaluation"] = true } : null,
    };
}
