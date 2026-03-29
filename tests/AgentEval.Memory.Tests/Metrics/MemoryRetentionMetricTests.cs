using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Metrics;

public class MemoryRetentionMetricTests
{
    private readonly MemoryRetentionMetric _metric;

    public MemoryRetentionMetricTests()
    {
        _metric = new MemoryRetentionMetric(NullLogger<MemoryRetentionMetric>.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_WithHighScore_ReturnsPass()
    {
        var result = CreateResult(90);
        var context = result.ToEvaluationContext();

        var metricResult = await _metric.EvaluateAsync(context);

        Assert.True(metricResult.Passed);
        Assert.Equal(90, metricResult.Score);
    }

    [Fact]
    public async Task EvaluateAsync_WithLowScore_ReturnsFail()
    {
        var result = CreateResult(50);
        var context = result.ToEvaluationContext();

        var metricResult = await _metric.EvaluateAsync(context);

        Assert.False(metricResult.Passed);
        Assert.Equal(50, metricResult.Score);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutMemoryResult_ReturnsFailWithNotFound()
    {
        var context = new EvaluationContext { Input = "test", Output = "test" };

        var metricResult = await _metric.EvaluateAsync(context);

        Assert.False(metricResult.Passed);
        Assert.Contains("not found", metricResult.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_IncludesDetailsInResult()
    {
        var result = CreateResult(90);
        var context = result.ToEvaluationContext();

        var metricResult = await _metric.EvaluateAsync(context);

        Assert.NotNull(metricResult.Details);
        Assert.True(metricResult.Details.ContainsKey("overall_score"));
        Assert.True(metricResult.Details.ContainsKey("queries_passed"));
        Assert.True(metricResult.Details.ContainsKey("total_queries"));
        Assert.True(metricResult.Details.ContainsKey("success_rate"));
        Assert.True(metricResult.Details.ContainsKey("found_facts"));
        Assert.True(metricResult.Details.ContainsKey("missing_facts"));
        Assert.True(metricResult.Details.ContainsKey("scenario_name"));
    }

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        Assert.Equal("code_memory_retention", _metric.Name);
    }

    private static MemoryEvaluationResult CreateResult(double score, string scenarioName = "Test")
    {
        var fact = MemoryFact.Create("test fact");
        var queryResult = new MemoryQueryResult
        {
            Query = MemoryQuery.Create("test?", fact),
            Response = "test response",
            Score = score,
            FoundFacts = score >= 50 ? new[] { fact } : Array.Empty<MemoryFact>(),
            MissingFacts = score < 50 ? new[] { fact } : Array.Empty<MemoryFact>(),
            ForbiddenFound = Array.Empty<MemoryFact>(),
            TokensUsed = 100
        };

        return new MemoryEvaluationResult
        {
            OverallScore = score,
            QueryResults = new[] { queryResult },
            FoundFacts = queryResult.FoundFacts,
            MissingFacts = queryResult.MissingFacts,
            ForbiddenFound = Array.Empty<MemoryFact>(),
            Duration = TimeSpan.FromSeconds(1),
            TokensUsed = 100,
            EstimatedCost = 0.001m,
            ScenarioName = scenarioName
        };
    }
}
