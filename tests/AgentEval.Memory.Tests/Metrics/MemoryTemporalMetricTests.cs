// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Metrics;

public class MemoryTemporalMetricTests
{
    private readonly MemoryTemporalMetric _metric;

    public MemoryTemporalMetricTests()
    {
        _metric = new MemoryTemporalMetric(NullLogger<MemoryTemporalMetric>.Instance);
    }

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        Assert.Equal("code_memory_temporal", _metric.Name);
    }

    [Fact]
    public void EstimatedCostPerEvaluation_IsNull()
    {
        Assert.Null(_metric.EstimatedCostPerEvaluation);
    }

    // --- Missing MemoryEvaluationResult ---

    [Fact]
    public async Task EvaluateAsync_WithoutMemoryResult_ReturnsFailWithNotFound()
    {
        var context = new EvaluationContext { Input = "test", Output = "test" };

        var result = await _metric.EvaluateAsync(context);

        Assert.False(result.Passed);
        Assert.Contains("not found", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // --- Non-temporal scenarios ---

    [Fact]
    public async Task EvaluateAsync_NonTemporalScenario_ReturnsPassWithNotApplicableFlag()
    {
        var memoryResult = CreateResult(score: 85, temporal: false);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.True(result.Passed);
        Assert.NotNull(result.Details);
        Assert.True(result.Details.ContainsKey("not_applicable"));
        Assert.Equal(true, result.Details["not_applicable"]);
        Assert.Equal("Non-temporal scenario", result.Details["reason"]);
    }

    [Fact]
    public async Task EvaluateAsync_NonTemporalScenario_ExplanationMentionsNotApplicable()
    {
        var memoryResult = CreateResult(score: 85, temporal: false);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.Contains("not applicable", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // --- Temporal scenarios (pass/fail) ---

    [Fact]
    public async Task EvaluateAsync_TemporalScenario_HighScore_ReturnsPass()
    {
        var memoryResult = CreateResult(score: 85, temporal: true, temporalScore: 80);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.True(result.Passed);
        Assert.Equal(80, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_TemporalScenario_LowScore_ReturnsFail()
    {
        var memoryResult = CreateResult(score: 40, temporal: true, temporalScore: 40);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.False(result.Passed);
        Assert.Equal(40, result.Score);
    }

    // --- Boxed metadata values correctly unboxed ---

    [Fact]
    public async Task EvaluateAsync_BoxedDoubleTemporalScore_ReflectedInDetails()
    {
        var memoryResult = CreateResult(score: 85, temporal: true, temporalScore: 92.5);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.Equal(92.5, result.Details["temporal_score"]);
    }

    [Fact]
    public async Task EvaluateAsync_BoxedDoubleTemporalAccuracy_ReflectedInDetails()
    {
        var memoryResult = CreateResult(score: 85, temporal: true, temporalScore: 80, temporalAccuracy: 75.0);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.Equal(75.0, result.Details["temporal_accuracy"]);
    }

    [Fact]
    public async Task EvaluateAsync_BoxedIntQueryCount_ReflectedInDetails()
    {
        var memoryResult = CreateResult(score: 85, temporal: true, temporalScore: 80, queryCount: 7);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.Equal(7, result.Details["temporal_query_count"]);
    }

    [Fact]
    public async Task EvaluateAsync_TemporalScenario_DetailsContainExpectedKeys()
    {
        var memoryResult = CreateResult(score: 85, temporal: true, temporalScore: 80);
        var context = memoryResult.ToEvaluationContext();

        var result = await _metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.True(result.Details.ContainsKey("temporal_score"));
        Assert.True(result.Details.ContainsKey("temporal_accuracy"));
        Assert.True(result.Details.ContainsKey("temporal_query_count"));
        Assert.True(result.Details.ContainsKey("overall_score"));
        Assert.True(result.Details.ContainsKey("scenario_name"));
    }

    // --- Helpers ---

    private static MemoryEvaluationResult CreateResult(
        double score,
        bool temporal,
        double? temporalScore = null,
        double? temporalAccuracy = null,
        int? queryCount = null)
    {
        var fact = MemoryFact.Create("test fact");
        var queryResult = new MemoryQueryResult
        {
            Query = MemoryQuery.Create("test?", fact),
            Response = "response",
            Score = score,
            FoundFacts = score >= 50 ? new[] { fact } : Array.Empty<MemoryFact>(),
            MissingFacts = score < 50 ? new[] { fact } : Array.Empty<MemoryFact>(),
            ForbiddenFound = Array.Empty<MemoryFact>(),
            TokensUsed = 100
        };

        var metadata = new Dictionary<string, object>();
        if (temporal)
        {
            metadata["TemporalEvaluation"] = true;
            if (temporalScore.HasValue)
                metadata["TemporalScore"] = temporalScore.Value;
            if (temporalAccuracy.HasValue)
                metadata["TemporalAccuracy"] = temporalAccuracy.Value;
            if (queryCount.HasValue)
                metadata["TemporalQueryCount"] = queryCount.Value;
        }

        return new MemoryEvaluationResult
        {
            OverallScore = score,
            QueryResults = new[] { queryResult },
            FoundFacts = queryResult.FoundFacts,
            MissingFacts = queryResult.MissingFacts,
            ForbiddenFound = Array.Empty<MemoryFact>(),
            Duration = TimeSpan.FromSeconds(1),
            TokensUsed = 100,
            EstimatedCost = 0,
            ScenarioName = "Test",
            Metadata = metadata.Count > 0 ? metadata : null
        };
    }
}
