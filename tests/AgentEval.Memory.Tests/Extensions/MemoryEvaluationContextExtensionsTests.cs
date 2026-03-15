using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Extensions;

public class MemoryEvaluationContextExtensionsTests
{
    [Fact]
    public void ToEvaluationContext_WithQueryResults_SetsInputFromLastQuery()
    {
        var result = CreateResult(90);

        var context = result.ToEvaluationContext();

        Assert.Equal("test?", context.Input);
    }

    [Fact]
    public void ToEvaluationContext_WithQueryResults_SetsOutputFromLastResponse()
    {
        var result = CreateResult(90);

        var context = result.ToEvaluationContext();

        Assert.Equal("test response", context.Output);
    }

    [Fact]
    public void ToEvaluationContext_SetsMemoryEvaluationResultProperty()
    {
        var result = CreateResult(90);

        var context = result.ToEvaluationContext();

        var stored = context.GetProperty<MemoryEvaluationResult>("MemoryEvaluationResult");
        Assert.NotNull(stored);
        Assert.Same(result, stored);
    }

    [Fact]
    public void ToEvaluationContext_WithNoQueries_UsesScenarioNameAsInput()
    {
        var result = new MemoryEvaluationResult
        {
            OverallScore = 0,
            QueryResults = Array.Empty<MemoryQueryResult>(),
            FoundFacts = Array.Empty<MemoryFact>(),
            MissingFacts = Array.Empty<MemoryFact>(),
            ForbiddenFound = Array.Empty<MemoryFact>(),
            Duration = TimeSpan.FromSeconds(1),
            TokensUsed = 0,
            EstimatedCost = 0m,
            ScenarioName = "MyScenario"
        };

        var context = result.ToEvaluationContext();

        Assert.Equal("MyScenario", context.Input);
        Assert.Equal(string.Empty, context.Output);
    }

    [Fact]
    public void ToEvaluationContext_WithNullResult_ThrowsArgumentNullException()
    {
        MemoryEvaluationResult result = null!;

        Assert.Throws<ArgumentNullException>(() => result.ToEvaluationContext());
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
