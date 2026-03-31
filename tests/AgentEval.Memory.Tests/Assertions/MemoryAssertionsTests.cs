using AgentEval.Assertions;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Assertions;

public class MemoryEvaluationAssertionsTests
{
    // -- HaveOverallScoreAtLeast --

    [Fact]
    public void HaveOverallScoreAtLeast_WhenScoreAboveThreshold_ShouldPass()
    {
        var result = CreateResult(overallScore: 90);

        result.Should().HaveOverallScoreAtLeast(80);
    }

    [Fact]
    public void HaveOverallScoreAtLeast_WhenScoreEqualsThreshold_ShouldPass()
    {
        var result = CreateResult(overallScore: 80);

        result.Should().HaveOverallScoreAtLeast(80);
    }

    [Fact]
    public void HaveOverallScoreAtLeast_WhenScoreBelowThreshold_ShouldThrow()
    {
        var result = CreateResult(overallScore: 50);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveOverallScoreAtLeast(80));
    }

    // -- HaveAllQueriesPassed --

    [Fact]
    public void HaveAllQueriesPassed_WhenAllPass_ShouldPass()
    {
        // Score 85 >= MinimumScore 80, so the query passes
        var result = CreateResult(overallScore: 85);

        result.Should().HaveAllQueriesPassed();
    }

    [Fact]
    public void HaveAllQueriesPassed_WhenSomeFail_ShouldThrow()
    {
        // Score 50 < MinimumScore 80, so the query fails
        var result = CreateResult(overallScore: 50);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveAllQueriesPassed());
    }

    // -- HaveAtLeastQueriesPassed --

    [Fact]
    public void HaveAtLeastQueriesPassed_WhenEnoughPass_ShouldPass()
    {
        var result = CreateResult(overallScore: 85);

        result.Should().HaveAtLeastQueriesPassed(1);
    }

    [Fact]
    public void HaveAtLeastQueriesPassed_WhenNotEnoughPass_ShouldThrow()
    {
        // Score 50 < MinimumScore 80, so 0 queries pass; requiring 1 should fail
        var result = CreateResult(overallScore: 50);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveAtLeastQueriesPassed(1));
    }

    // -- NotHaveRecalledForbiddenFacts --

    [Fact]
    public void NotHaveRecalledForbiddenFacts_WhenNoneForbidden_ShouldPass()
    {
        var result = CreateResult(forbiddenFoundCount: 0);

        result.Should().NotHaveRecalledForbiddenFacts();
    }

    [Fact]
    public void NotHaveRecalledForbiddenFacts_WhenForbiddenPresent_ShouldThrow()
    {
        var result = CreateResult(forbiddenFoundCount: 2);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().NotHaveRecalledForbiddenFacts());
    }

    // -- HaveCompletedWithin --

    [Fact]
    public void HaveCompletedWithin_WhenWithinTime_ShouldPass()
    {
        var result = CreateResult(duration: TimeSpan.FromSeconds(1));

        result.Should().HaveCompletedWithin(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HaveCompletedWithin_WhenExceededTime_ShouldThrow()
    {
        var result = CreateResult(duration: TimeSpan.FromSeconds(10));

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveCompletedWithin(TimeSpan.FromSeconds(5)));
    }

    // -- HaveUsedFewerTokens --

    [Fact]
    public void HaveUsedFewerTokens_WhenBelowLimit_ShouldPass()
    {
        var result = CreateResult(tokensUsed: 100);

        result.Should().HaveUsedFewerTokens(500);
    }

    [Fact]
    public void HaveUsedFewerTokens_WhenAboveLimit_ShouldThrow()
    {
        var result = CreateResult(tokensUsed: 600);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveUsedFewerTokens(500));
    }

    // -- HaveCostLessThan --

    [Fact]
    public void HaveCostLessThan_WhenBelowLimit_ShouldPass()
    {
        var result = CreateResult(estimatedCost: 0.01m);

        result.Should().HaveCostLessThan(0.05m);
    }

    [Fact]
    public void HaveCostLessThan_WhenAboveLimit_ShouldThrow()
    {
        var result = CreateResult(estimatedCost: 0.10m);

        Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveCostLessThan(0.05m));
    }

    // -- Chaining --

    [Fact]
    public void Chaining_MultiplePassingAssertions_ShouldPass()
    {
        var result = CreateResult(overallScore: 85, forbiddenFoundCount: 0);

        result.Should()
            .HaveOverallScoreAtLeast(50)
            .NotHaveRecalledForbiddenFacts();
    }

    // -- Exception base type --

    [Fact]
    public void FailedAssertion_ShouldThrowMemoryAssertionException_InheritingAgentEvalAssertionException()
    {
        var result = CreateResult(overallScore: 10);

        var ex = Assert.Throws<MemoryAssertionException>(() =>
            result.Should().HaveOverallScoreAtLeast(90));

        Assert.IsAssignableFrom<AgentEvalAssertionException>(ex);
    }

    // -- Helper --

    private static MemoryEvaluationResult CreateResult(
        double overallScore = 85,
        int foundFactCount = 2,
        int missingFactCount = 0,
        int forbiddenFoundCount = 0,
        TimeSpan? duration = null,
        int tokensUsed = 100,
        decimal estimatedCost = 0.01m)
    {
        var foundFacts = Enumerable.Range(0, foundFactCount).Select(i => MemoryFact.Create($"Found fact {i}")).ToList();
        var missingFacts = Enumerable.Range(0, missingFactCount).Select(i => MemoryFact.Create($"Missing fact {i}")).ToList();
        var forbiddenFound = Enumerable.Range(0, forbiddenFoundCount).Select(i => MemoryFact.Create($"Forbidden fact {i}")).ToList();

        var queryResults = new List<MemoryQueryResult>
        {
            new()
            {
                Query = MemoryQuery.Create("Test question?", foundFacts.Concat(missingFacts).Select(f => f).ToArray()),
                Response = "Test response with Found fact 0 and Found fact 1",
                Score = overallScore,
                FoundFacts = foundFacts,
                MissingFacts = missingFacts,
                ForbiddenFound = forbiddenFound,
                Explanation = "Test",
                TokensUsed = tokensUsed
            }
        };

        return new MemoryEvaluationResult
        {
            OverallScore = overallScore,
            QueryResults = queryResults,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound,
            Duration = duration ?? TimeSpan.FromSeconds(1),
            TokensUsed = tokensUsed,
            EstimatedCost = estimatedCost,
            ScenarioName = "Test Scenario"
        };
    }
}

public class MemoryQueryAssertionsTests
{
    // -- HavePassed --

    [Fact]
    public void HavePassed_WhenScoreAboveMinimum_ShouldPass()
    {
        // MinimumScore defaults to 80; score 85 >= 80
        var queryResult = CreateQueryResult(score: 85);

        queryResult.Should().HavePassed();
    }

    [Fact]
    public void HavePassed_WhenScoreEqualsMinimum_ShouldPass()
    {
        var queryResult = CreateQueryResult(score: 80);

        queryResult.Should().HavePassed();
    }

    [Fact]
    public void HavePassed_WhenScoreBelowMinimum_ShouldThrow()
    {
        var queryResult = CreateQueryResult(score: 50);

        Assert.Throws<MemoryAssertionException>(() =>
            queryResult.Should().HavePassed());
    }

    // -- HaveScoreAtLeast --

    [Fact]
    public void HaveScoreAtLeast_WhenScoreAboveThreshold_ShouldPass()
    {
        var queryResult = CreateQueryResult(score: 90);

        queryResult.Should().HaveScoreAtLeast(70);
    }

    [Fact]
    public void HaveScoreAtLeast_WhenScoreEqualsThreshold_ShouldPass()
    {
        var queryResult = CreateQueryResult(score: 70);

        queryResult.Should().HaveScoreAtLeast(70);
    }

    [Fact]
    public void HaveScoreAtLeast_WhenScoreBelowThreshold_ShouldThrow()
    {
        var queryResult = CreateQueryResult(score: 60);

        Assert.Throws<MemoryAssertionException>(() =>
            queryResult.Should().HaveScoreAtLeast(70));
    }

    // -- HaveFoundAllExpectedFacts --

    [Fact]
    public void HaveFoundAllExpectedFacts_WhenNoMissing_ShouldPass()
    {
        var queryResult = CreateQueryResult(missingFactCount: 0);

        queryResult.Should().HaveFoundAllExpectedFacts();
    }

    [Fact]
    public void HaveFoundAllExpectedFacts_WhenSomeMissing_ShouldThrow()
    {
        var queryResult = CreateQueryResult(missingFactCount: 1);

        Assert.Throws<MemoryAssertionException>(() =>
            queryResult.Should().HaveFoundAllExpectedFacts());
    }

    // -- HaveResponseContaining --

    [Fact]
    public void HaveResponseContaining_WhenTextPresent_ShouldPass()
    {
        var queryResult = CreateQueryResult();

        queryResult.Should().HaveResponseContaining("Found fact 0");
    }

    [Fact]
    public void HaveResponseContaining_WhenTextNotPresent_ShouldThrow()
    {
        var queryResult = CreateQueryResult();

        Assert.Throws<MemoryAssertionException>(() =>
            queryResult.Should().HaveResponseContaining("nonexistent text"));
    }

    // -- Helper --

    private static MemoryQueryResult CreateQueryResult(
        double score = 85,
        int foundFactCount = 2,
        int missingFactCount = 0,
        int forbiddenFoundCount = 0)
    {
        var foundFacts = Enumerable.Range(0, foundFactCount).Select(i => MemoryFact.Create($"Found fact {i}")).ToList();
        var missingFacts = Enumerable.Range(0, missingFactCount).Select(i => MemoryFact.Create($"Missing fact {i}")).ToList();
        var forbiddenFound = Enumerable.Range(0, forbiddenFoundCount).Select(i => MemoryFact.Create($"Forbidden fact {i}")).ToList();

        return new MemoryQueryResult
        {
            Query = MemoryQuery.Create("Test question?", foundFacts.Concat(missingFacts).Select(f => f).ToArray()),
            Response = "Test response with Found fact 0 and Found fact 1",
            Score = score,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound,
            Explanation = "Test",
            TokensUsed = 100
        };
    }
}
