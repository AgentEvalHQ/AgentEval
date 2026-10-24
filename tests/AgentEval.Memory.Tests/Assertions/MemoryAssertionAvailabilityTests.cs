// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using AgentEval.Output;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Memory.Tests.Assertions;

public class MemoryAssertionAvailabilityTests
{
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
