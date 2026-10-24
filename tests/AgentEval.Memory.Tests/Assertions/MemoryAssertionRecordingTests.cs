// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Memory.Tests.Assertions;

/// <summary>
/// AE-01 coverage for the memory assertion family: passes are recorded, failures are recorded
/// under the assertion's own name, and a collecting scope never throws. This is the regression
/// guard for the probe instrumentation in <c>MemoryAssertions</c> — an assertion whose return was
/// not routed through <c>probe.Complete(...)</c> would record "could not decide" instead of the
/// pass it earned.
/// </summary>
public class MemoryAssertionRecordingTests
{
    [Fact]
    public void PassingMemoryAssertions_EachRecordExactlyOnePass()
    {
        var result = CreateResult(overallScore: 90);

        using var scope = AgentEvalScope.Collecting();
        result.Should().HaveOverallScoreAtLeast(80);
        result.Should().HaveAllQueriesPassed();
        result.Should().HaveAtLeastQueriesPassed(1);
        result.Should().NotHaveRecalledForbiddenFacts();
        result.Should().HaveCompletedWithin(TimeSpan.FromMinutes(1));
        result.Should().HaveUsedFewerTokens(10_000);
        result.Should().HaveCostLessThan(1.0m);
        scope.Dispose();

        Assert.Equal(7, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void FailingMemoryAssertion_IsRecordedAndDoesNotThrowInACollectingScope()
    {
        var result = CreateResult(overallScore: 10);

        var scope = AgentEvalScope.Collecting();
        result.Should().HaveOverallScoreAtLeast(90);

        var thrown = Record.Exception(() => scope.Dispose());

        Assert.Null(thrown);
        var row = Assert.Single(scope.Results);
        Assert.Equal("HaveOverallScoreAtLeast", row.Assertion);
        Assert.Equal(AssertionOutcome.Failed, row.Outcome);
    }

    [Fact]
    public void FailingMemoryAssertion_WithNoScope_StillThrows()
    {
        var result = CreateResult(overallScore: 10);

        Assert.Throws<MemoryAssertionException>(() => result.Should().HaveOverallScoreAtLeast(90));
    }

    [Fact]
    public void CostNotPriced_InACollectingScope_IsInconclusive()
    {
        // T63: an unpriced cost cannot show the budget was kept. It used to be a flat-rate guess, so this passed.
        var result = CreateResult(overallScore: 90, estimatedCost: null);

        var scope = AgentEvalScope.Collecting();
        result.Should().HaveCostLessThan(1.0m);
        scope.Dispose();

        var row = Assert.Single(scope.Results);
        Assert.Equal("HaveCostLessThan", row.Assertion);
        Assert.Equal(AssertionOutcome.Inconclusive, row.Outcome);
    }

    [Fact]
    public void CostNotPriced_WithNoScope_Fails()
    {
        // Nothing can record an undecided result outside a scope, so the check fails rather than passing silently.
        var result = CreateResult(overallScore: 90, estimatedCost: null);

        var ex = Assert.Throws<MemoryAssertionException>(() => result.Should().HaveCostLessThan(1.0m));
        Assert.Contains("not priced", ex.Message);
    }

    private static MemoryEvaluationResult CreateResult(double overallScore, decimal? estimatedCost = 0.01m)
    {
        var foundFacts = new List<MemoryFact> { MemoryFact.Create("Found fact 0") };

        var queryResults = new List<MemoryQueryResult>
        {
            new()
            {
                Query = MemoryQuery.Create("Test question?", foundFacts.ToArray()),
                Response = "Test response with Found fact 0",
                Score = overallScore,
                FoundFacts = foundFacts,
                MissingFacts = [],
                ForbiddenFound = [],
                Explanation = "Test",
                TokensUsed = 100
            }
        };

        return new MemoryEvaluationResult
        {
            OverallScore = overallScore,
            QueryResults = queryResults,
            FoundFacts = foundFacts,
            MissingFacts = [],
            ForbiddenFound = [],
            Duration = TimeSpan.FromSeconds(1),
            TokensUsed = 100,
            EstimatedCost = estimatedCost,
            ScenarioName = "Test Scenario"
        };
    }
}
