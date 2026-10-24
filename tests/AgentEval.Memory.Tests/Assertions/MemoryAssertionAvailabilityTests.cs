// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using AgentEval.Output;
using AgentEval.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Assertions;

public class MemoryAssertionAvailabilityTests
{
    [Theory]
    [InlineData("score: 85", 0)]
    [InlineData("score: 85", 2)]
    [InlineData("85/100", 0)]
    [InlineData("85/100", 2)]
    [InlineData("{\"score\":85}", 0)]
    [InlineData("{\"score\":85}", 2)]
    public async Task FactAssertions_ScoreWithoutFactAssessment_WithholdsFactPassButKeepsNumericScore(string reply, int assertion)
    {
        var result = await RunAsync(new FakeChatClient(reply), Query());
        Assert.True(result.QueryResults[0].Measured);
        Assert.True(result.QueryResults[0].Passed);
        Assert.Equal(85, result.OverallScore);
        result.QueryResults[0].Should().HaveScoreAtLeast(80);
        result.Should().HaveAllQueriesPassed();

        var scope = AgentEvalScope.Collecting();
        Check(result, assertion);
        scope.Dispose();

        Assert.Equal(AssertionOutcome.Inconclusive, Assert.Single(scope.Results).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FactAssertions_ScoreOnlyJsonWithoutScope_ThrowsForMissingEvidence(int assertion)
    {
        var result = await RunAsync(new FakeChatClient("{\"score\":85}"), Query());

        Assert.Throws<MemoryAssertionException>(() => Check(result, assertion));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"expected\"]")]
    public async Task HaveFoundAllExpectedFacts_EmptyOrPartialClassification_CannotProveAllFacts(string found)
    {
        var reply = "{\"score\":90,\"found_facts\":" + found + ",\"missing_facts\":[],\"forbidden_found\":[]}";
        var result = await RunAsync(new FakeChatClient(reply), MemoryQuery.Create("Recall?",
            [MemoryFact.Create("expected"), MemoryFact.Create("second")], [MemoryFact.Create("secret")]));
        var scope = AgentEvalScope.Collecting();
        result.QueryResults[0].Should().HaveFoundAllExpectedFacts();
        result.Should().NotHaveRecalledForbiddenFacts();
        scope.Dispose();

        Assert.Equal(new[] { AssertionOutcome.Inconclusive, AssertionOutcome.Passed }, scope.Results.Select(row => row.Outcome));
        Assert.Equal(90, result.OverallScore);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"forbidden_found\":null")]
    [InlineData(",\"forbidden_found\":[\"\"]")]
    [InlineData(",\"forbidden_found\":[\"unknown entity\"]")]
    public async Task NotHaveRecalledForbiddenFacts_OmittedOrUnusableAssessment_IsInconclusive(string field)
    {
        var result = await RunAsync(new FakeChatClient("{\"score\":90,\"found_facts\":[\"expected\"]" + field + "}"), Query());
        Assert.Equal(90, result.OverallScore);
        var scope = AgentEvalScope.Collecting();
        result.Should().NotHaveRecalledForbiddenFacts();
        scope.Dispose();

        Assert.Equal(AssertionOutcome.Inconclusive, Assert.Single(scope.Results).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactAssertions_ExplicitCompleteAssessment_RemainsValidAfterSerialization(bool forbidden)
    {
        var reply = "{\"score\":90,\"found_facts\":[\"expected\"],\"missing_facts\":[],\"forbidden_found\":"
            + (forbidden ? "[\"secret\"]" : "[]") + "}";
        var original = await RunAsync(new FakeChatClient(reply), Query());
        var result = System.Text.Json.JsonSerializer.Deserialize<MemoryEvaluationResult>(System.Text.Json.JsonSerializer.Serialize(original))!;
        var scope = AgentEvalScope.Collecting();
        result.QueryResults[0].Should().HaveFoundAllExpectedFacts();
        result.Should().NotHaveRecalledForbiddenFacts();
        result.Should().HaveAllQueriesPassed(); // Numeric threshold policy is separate from fact claims.
        scope.Dispose();

        Assert.Equal(new[] { AssertionOutcome.Passed, forbidden ? AssertionOutcome.Failed : AssertionOutcome.Passed, AssertionOutcome.Passed },
            scope.Results.Select(row => row.Outcome));
    }

    [Theory]
    [InlineData("{\"score\":90}")]
    [InlineData("score: 90")]
    [InlineData("{\"score\":90,\"found_facts\":[],\"missing_facts\":[],\"forbidden_found\":[]}")]
    public async Task NoiseMetric_MissingRelevantFactEvidence_CannotAddBonusOrPublishFactRates(string reply)
    {
        var memory = await RunNoiseAsync(reply, Query());
        var result = await Noise().EvaluateAsync(memory.ToEvaluationContext());

        Assert.Equal(90, memory.OverallScore);
        Assert.True(memory.QueryResults[0].Passed);
        Assert.False(result.Measured);
        Assert.False(result.Passed);
        Assert.Equal(90d, result.Details!["overall_score"]);
        Assert.False(result.Details.ContainsKey("false_positive_rate"));
        Assert.False(result.Details.ContainsKey("signal_detection_rate"));
    }

    [Theory]
    [InlineData(false, 94.5)]
    [InlineData(true, 85)]
    public async Task NoiseMetric_CompleteFactAssessment_RetainsExistingScorePolicy(bool forbidden, double expected)
    {
        var memory = await RunNoiseAsync("{\"score\":90,\"found_facts\":[\"expected\"],\"missing_facts\":[],\"forbidden_found\":"
            + (forbidden ? "[\"secret\"]" : "[]") + "}", Query());
        var result = await Noise().EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
        Assert.Equal(expected, result.Score);
        Assert.Equal(forbidden ? 100d : 0d, result.Details!["false_positive_rate"]);
        Assert.Equal(100d, result.Details["signal_detection_rate"]);
    }

    [Fact]
    public async Task NoiseMetric_NoExpectedFacts_DoesNotInventAZeroDenominatorRate()
    {
        var memory = await RunNoiseAsync("{\"score\":90,\"forbidden_found\":[]}", MemoryQuery.Create("No facts"));
        var result = await Noise().EvaluateAsync(memory.ToEvaluationContext());

        Assert.False(result.Measured);
        Assert.False(result.Details!.ContainsKey("false_positive_rate"));
        Assert.False(result.Details.ContainsKey("signal_detection_rate"));
    }

    [Fact]
    public async Task NoiseMetric_NonNoiseScenarioWithoutFactDiagnostics_RetainsNumericPassThrough()
    {
        var memory = await RunAsync(new FakeChatClient("{\"score\":90}"), Query());
        var result = await Noise().EvaluateAsync(memory.ToEvaluationContext());

        Assert.True(result.Measured);
        Assert.Equal(90, result.Score);
    }

    private static MemoryNoiseResilienceMetric Noise() => new(NullLogger<MemoryNoiseResilienceMetric>.Instance);

    private static Task<MemoryEvaluationResult> RunNoiseAsync(string reply, MemoryQuery query) =>
        MemoryTestRunner.Create(new FakeChatClient(reply)).RunAsync(new RespondingAgent(), MemoryTestScenario.Create("noise", [], [query]));

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task FactAndScoreAssertions_UnscoredJudge_CannotRecordACleanPass(int assertion, bool throws)
    {
        var result = await RunAsync(new FakeChatClient("No usable score") { ThrowOnNextCall = throws }, Query());
        Assert.False(Assert.Single(result.QueryResults).Measured);

        var scope = AgentEvalScope.Collecting();
        Check(result, assertion);
        scope.Dispose();

        // A thrown judge already supplies an explicit missing-fact list. Preserve that existing failure.
        var expected = assertion == 2 && throws ? AssertionOutcome.Failed : AssertionOutcome.Inconclusive;
        Assert.Equal(expected, Assert.Single(scope.Results).Outcome);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task FactAndScoreAssertions_UnscoredJudgeWithoutScope_Throws(int assertion, bool throws)
    {
        var result = await RunAsync(new FakeChatClient("No usable score") { ThrowOnNextCall = throws }, Query());

        Assert.Throws<MemoryAssertionException>(() => Check(result, assertion));
    }

    [Fact]
    public async Task FactAssertions_UnmeasuredQueryWithNoRequestedFacts_RemainVacuousAndObservedChecksWork()
    {
        var result = await RunAsync(new FakeChatClient("No usable score"), MemoryQuery.Create("No facts requested"));

        var scope = AgentEvalScope.Collecting();
        result.Should().NotHaveRecalledForbiddenFacts();
        result.QueryResults[0].Should().HaveFoundAllExpectedFacts();
        result.QueryResults[0].Should().HaveResponseContaining("observed response");
        result.Should().HaveCompletedWithin(TimeSpan.FromMinutes(1));
        scope.Dispose();

        Assert.Equal(4, scope.Results.Count);
        Assert.All(scope.Results, row => Assert.Equal(AssertionOutcome.Passed, row.Outcome));
    }

    [Fact]
    public async Task FactAssertions_KnownFailureBesideAnUnmeasuredQuery_RemainsFailureAndMeasuredZeroIsReal()
    {
        var client = new FakeChatClient("No usable score", """
            {"score":0,"found_facts":[],"missing_facts":["expected"],"forbidden_found":["secret"],"explanation":"known failure"}
            """);
        var result = await RunAsync(client, Query(), Query());
        Assert.False(result.QueryResults[0].Measured);
        Assert.True(result.QueryResults[1].Measured);

        var scope = AgentEvalScope.Collecting();
        result.Should().NotHaveRecalledForbiddenFacts();
        result.QueryResults[1].Should().HaveFoundAllExpectedFacts();
        result.QueryResults[1].Should().HaveScoreAtLeast(0);
        scope.Dispose();

        Assert.Equal(new[] { AssertionOutcome.Failed, AssertionOutcome.Failed, AssertionOutcome.Passed },
            scope.Results.Select(row => row.Outcome));
    }

    private static void Check(MemoryEvaluationResult result, int assertion)
    {
        switch (assertion)
        {
            case 0: result.Should().NotHaveRecalledForbiddenFacts(because: "isolation matters"); break;
            case 1: result.QueryResults[0].Should().HaveScoreAtLeast(0, because: "a placeholder is not zero"); break;
            case 2: result.QueryResults[0].Should().HaveFoundAllExpectedFacts(because: "recall matters"); break;
            default: throw new ArgumentOutOfRangeException(nameof(assertion));
        }
    }

    private static MemoryQuery Query() => MemoryQuery.Create("Recall?", [MemoryFact.Create("expected")], [MemoryFact.Create("secret")]);

    private static Task<MemoryEvaluationResult> RunAsync(FakeChatClient client, params MemoryQuery[] queries) =>
        MemoryTestRunner.Create(client).RunAsync(new RespondingAgent(), MemoryTestScenario.Create("availability", [], queries));

    private sealed class RespondingAgent : IEvaluableAgent
    {
        public string Name => "Offline memory agent";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "observed response" });
    }
}
