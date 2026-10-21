// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Models;

public class MemoryResultsNoScoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemoryResult_NothingMeasured_HasNoScoreOrRates(bool empty)
    {
        var result = Memory(empty ? [] : [Query(measured: false)]);
        Assert.Null(result.OverallScore);
        Assert.Null(result.RetentionRate);
        Assert.Null(result.SuccessRate);
        Assert.Contains("n/a", result.ToString());
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(result).GetProperty("OverallScore").ValueKind);
    }

    [Fact]
    public void HaveOverallScoreAtLeast_UnmeasuredAtZeroThreshold_RejectsAbsence()
    {
        var error = Assert.Throws<MemoryAssertionException>(() => Memory([]).Should().HaveOverallScoreAtLeast(0, "a grade is required"));
        Assert.Contains("not measured", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a grade is required", error.Message);
    }

    [Fact]
    public void CrossSession_NothingMeasured_HasNoScore()
    {
        var result = new CrossSessionResult
        {
            ScenarioName = "empty", Passed = false, OverallScore = 99, FactResults = [],
            SessionResetSupported = true, Duration = TimeSpan.Zero,
        };
        Assert.Null(result.OverallScore);
    }

    [Fact]
    public void ReachBack_NothingMeasured_HasNoScoreOrDepth()
    {
        var result = new ReachBackResult { Fact = new() { Content = "fact" }, DepthResults = [], Duration = TimeSpan.Zero };
        Assert.Null(result.OverallScore);
        Assert.Null(result.MaxReliableDepth);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Reducer_NothingMeasured_HasNoFidelity()
    {
        var result = new ReducerEvaluationResult { ScenarioName = "empty", FactResults = [], Duration = TimeSpan.Zero };
        Assert.Null(result.FidelityScore);
        Assert.False(result.Passed);
    }

    [Fact]
    public void GuessingBaseline_EmptyVertical_HasNoShare()
    {
        var result = new TypedMemEvalGuessingBaseline { QuestionsTotal = 0, QuestionsWithDeclaredFloor = 0, MaximumCorrectFromGuessing = 0 };
        Assert.Null(result.ShareOfVertical);
    }

    [Fact]
    public void MemoryResult_MeasuredZero_RemainsAReading()
    {
        var result = Memory([Query(measured: true)], 0);
        Assert.Equal(0, result.OverallScore);
        Assert.Equal(0, result.RetentionRate);
        Assert.Equal(0, result.SuccessRate);
        result.Should().HaveOverallScoreAtLeast(0);
    }

    [Fact]
    public void ReachBack_MeasuredFailure_PreservesZeroScoreAndDepth()
    {
        var result = new ReachBackResult
        {
            Fact = new() { Content = "fact" }, Duration = TimeSpan.Zero,
            DepthResults = [new() { Depth = 1, Score = 0, Response = "unknown", Duration = TimeSpan.Zero }],
        };
        Assert.Equal(0, result.OverallScore);
        Assert.Equal(0, result.MaxReliableDepth);
    }

    private static MemoryQueryResult Query(bool measured) => new()
    {
        Query = new() { Question = "q", ExpectedFacts = [] }, Response = "a", Score = 0,
        Measured = measured, FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
    };

    private static MemoryEvaluationResult Memory(IReadOnlyList<MemoryQueryResult> queries, double score = 99) => new()
    {
        OverallScore = score, QueryResults = queries, FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
        Duration = TimeSpan.Zero, ScenarioName = "memory",
    };
}
