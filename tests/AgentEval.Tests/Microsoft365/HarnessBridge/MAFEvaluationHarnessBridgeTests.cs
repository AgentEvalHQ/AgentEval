// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Models;
using AgentEval.Testing;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.HarnessBridge;

/// <summary>
/// The Microsoft 365 provider through the REAL <see cref="MAFEvaluationHarness"/> and the REAL
/// <see cref="WorkIqEvaluableAgent"/> (built by <see cref="Microsoft365AgentFactory.BuildLive"/>; only the token provider
/// and the innermost HTTP handler are fakes), in both directions: harness → agent (what the harness sends, how many turns,
/// which context) and agent → harness (what the harness records for a completed, a suspended, a failed, a capped and a
/// cancelled turn). A non-completed turn is an error row, never a graded answer; its zero score is a placeholder and is
/// never asserted as a measurement.
/// </summary>
public sealed class MAFEvaluationHarnessBridgeTests
{
    private const string Question = "Summarize the status of Project Atlas and cite the sources.";
    private const string Criterion = "States the project's status";
    private const string PassingVerdict =
        """{"criteriaResults":[{"criterion":"States the project's status","met":true,"explanation":"The answer states the schedule and the open risks."}],"overallScore":92,"summary":"Answers the status question with its sources."}""";

    private static TestCase StatusCase(IReadOnlyList<string>? criteria = null) => new()
    {
        Name = "atlas-status",
        Input = Question,
        EvaluationCriteria = criteria,
    };

    private static DatasetTestCase[] ThreeCases() =>
    [
        new DatasetTestCase { Id = "c1", Input = Question },
        new DatasetTestCase { Id = "c2", Input = "And the risks?" },
        new DatasetTestCase { Id = "c3", Input = "Who owns the vendor contract?" },
    ];

    // ---------------------------------------------------------------------------------------------------------
    // (a) completed turn → graded answer
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunEvaluationAsync_CompletedTurnWithJudge_PassesOnTheFixtureAnswerWithNoToolTelemetry()
    {
        var judge = new FakeChatClient(PassingVerdict);
        var harness = new MAFEvaluationHarness(judge, verbose: false);
        var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture));
        await using (rig)
        {
            var result = await harness.RunEvaluationAsync(
                rig.Agent,
                StatusCase([Criterion]),
                new EvaluationOptions { TrackTools = true, TrackPerformance = true });

            Assert.False(result.HasError);
            Assert.True(result.Passed);
            Assert.Equal(92, result.Score);
            Assert.Equal(LiveAgentRig.CompletedFixtureAnswer(), result.ActualOutput);

            // Nothing is inferred: Work IQ returns no chat messages, so no tool recorder ran and no timeline is claimed.
            Assert.Null(result.ToolUsage);
            Assert.Null(result.Timeline);

            // Harness → judge: the judge graded exactly the agent's answer, once.
            Assert.Equal(1, judge.CallCount);
            var prompt = string.Join("\n", judge.ReceivedMessages.Single().Select(m => m.Text));
            Assert.Contains("Two risks stay open", prompt, StringComparison.Ordinal);

            // Harness → agent: one card discovery, one send, both authenticated, and one completed turn observed.
            Assert.Equal(1, rig.Handler.CardRequestCount);
            Assert.Single(rig.Sends());
            Assert.All(rig.Handler.Requests, r => Assert.Equal("Bearer", r.AuthorizationScheme));
            var turn = Assert.Single(rig.Turns);
            Assert.Equal(WorkIqTurnOutcome.Completed, turn.Outcome);
            Assert.Equal(1, rig.Agent.TurnsUsed);

            // The trace file was written (one flushed line per event) and never carries the token.
            var diagnostics = rig.ReadDiagnosticsFiles();
            Assert.Contains("turn.outcome", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(LiveAgentRig.FakeToken, diagnostics, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // (b) input-required → error row, never graded
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunEvaluationAsync_InputRequiredTurn_IsAnErrorRowTheJudgeNeverSees()
    {
        var judge = new FakeChatClient(PassingVerdict);
        var harness = new MAFEvaluationHarness(judge, verbose: false);
        await using var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.InputRequiredFixture));

        var result = await harness.RunEvaluationAsync(rig.Agent, StatusCase([Criterion]));

        Assert.True(result.HasError);
        Assert.False(result.Passed);
        var notCompleted = Assert.IsType<WorkIqTurnNotCompletedException>(result.Error);
        Assert.Equal(WorkIqTurnOutcome.InputRequired, notCompleted.TurnResult.Outcome);
        Assert.NotNull(notCompleted.TurnResult.PendingInteraction);
        Assert.Null(notCompleted.TurnResult.AnswerText);
        Assert.Null(result.ActualOutput);
        Assert.Equal(0, judge.CallCount);

        // The confirmation card is decoded under the default public profile too (it arrives with the status).
        var approvals = notCompleted.TurnResult.Evidence.ApprovalIntents;
        Assert.Equal(EvidenceAvailability.Provided, approvals.Availability);
        Assert.Equal("CreateDemoEvent", Assert.Single(approvals.Items).Tool.FunctionDisplayName.Value);
        Assert.Equal(EvidenceAvailability.NotRequested, notCompleted.TurnResult.Evidence.ToolExecutions.Availability);

        // The pause was not answered and the input was not resent.
        Assert.Single(rig.Sends());
        Assert.Equal(0, rig.Handler.CountOf("GetTask"));
        Assert.Same(notCompleted.TurnResult, Assert.Single(rig.Turns));
    }

    // ---------------------------------------------------------------------------------------------------------
    // (c) failed with a partial artifact → error row, no partial text anywhere
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunEvaluationAsync_FailedTurnWithPartialArtifact_IsAnErrorRowWithoutThePartialText()
    {
        const string partial = "Project Atlas is on schedule: the design review closed on 3 November 2026 and ";
        var harness = new MAFEvaluationHarness(verbose: false);
        await using var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.FailedFixture));

        var result = await harness.RunEvaluationAsync(rig.Agent, StatusCase());

        Assert.True(result.HasError);
        Assert.False(result.Passed);
        var notCompleted = Assert.IsType<WorkIqTurnNotCompletedException>(result.Error);
        Assert.Equal(WorkIqTurnOutcome.Failed, notCompleted.TurnResult.Outcome);
        Assert.Null(notCompleted.TurnResult.AnswerText);
        Assert.Null(result.ActualOutput);
        Assert.DoesNotContain(partial.Trim(), result.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(partial.Trim(), notCompleted.Message, StringComparison.Ordinal);
        Assert.True(notCompleted.TurnResult.Evidence.Answer is null or { IsFinal: false });
        Assert.Single(rig.Sends());
    }

    // ---------------------------------------------------------------------------------------------------------
    // (d) RunBatchAsync and the conversation policy
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunBatchAsync_FreshContextPerInvocation_ThreeSendsNoneCarriesAContextId()
    {
        var harness = new MAFEvaluationHarness(verbose: false);
        await using var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture),
            new WorkIqOptions { ConversationPolicy = ConversationPolicy.FreshContextPerInvocation });
        var observed = new List<TestResult>();

        var summary = await harness.RunBatchAsync(rig.Agent, ThreeCases(), null, CancellationToken.None, observed.Add);

        Assert.Equal(3, summary.Results.Count);
        Assert.Equal(3, observed.Count);
        Assert.All(summary.Results, r => Assert.True(r.Passed));
        var sends = rig.Sends();
        Assert.Equal(3, sends.Count);
        Assert.All(sends, s => Assert.Null(LiveAgentRig.ContextIdOf(s)));
        Assert.Equal(3, rig.Turns.Select(t => t.Identity.ClientMessageId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, rig.Handler.CardRequestCount);
    }

    [Fact]
    public async Task RunBatchAsync_ContinueContext_SecondAndThirdSendsCarryTheFirstContextId()
    {
        var harness = new MAFEvaluationHarness(verbose: false);
        await using var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture),
            new WorkIqOptions { ConversationPolicy = ConversationPolicy.ContinueContext });

        var summary = await harness.RunBatchAsync(rig.Agent, ThreeCases());

        Assert.Equal(3, summary.Results.Count);
        var sends = rig.Sends();
        Assert.Equal(3, sends.Count);
        Assert.Null(LiveAgentRig.ContextIdOf(sends[0]));
        var first = rig.Turns[0].ContextId;
        Assert.Equal(LiveAgentRig.FixtureContextId, first);
        Assert.Equal(first, LiveAgentRig.ContextIdOf(sends[1]));
        Assert.Equal(first, LiveAgentRig.ContextIdOf(sends[2]));
        Assert.All(sends, s => Assert.False(LiveAgentRig.MessageOf(s).ContainsKey("taskId") && LiveAgentRig.MessageOf(s)["taskId"] is not null));
    }

    // ---------------------------------------------------------------------------------------------------------
    // (e) the turn cap is fatal to the batch
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunBatchAsync_MaxTurnsTwo_ThirdCaseThrowsTheBudgetExceptionOutOfTheBatch()
    {
        var harness = new MAFEvaluationHarness(verbose: false);
        await using var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture),
            new WorkIqOptions { MaxTurns = 2 });
        var observed = new List<TestResult>();

        var budget = await Assert.ThrowsAsync<WorkIqTurnBudgetExceededException>(
            () => harness.RunBatchAsync(rig.Agent, ThreeCases(), null, CancellationToken.None, observed.Add));

        Assert.IsAssignableFrom<FatalEvaluationException>(budget);
        Assert.Equal(2, budget.TurnsUsed);
        Assert.Equal(2, budget.MaxTurns);
        Assert.Equal(2, observed.Count);
        Assert.Equal(2, rig.Sends().Count);      // the third send never fired
        Assert.Equal(2, rig.Turns.Count);
        Assert.Equal(2, rig.Agent.TurnsUsed);
    }

    // ---------------------------------------------------------------------------------------------------------
    // (f) StochasticRunner with a factory → a fresh conversation per replicate
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StochasticRunner_AgentFactory_EachReplicateRunsOnAFreshConversation()
    {
        var handler = LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture);
        var factory = new LiveAgentFactory(handler);
        try
        {
            var runner = new StochasticRunner(new MAFEvaluationHarness(verbose: false));

            var result = await runner.RunStochasticTestAsync(factory, StatusCase(), new StochasticOptions(Runs: 3, SuccessRateThreshold: 0.5));

            Assert.Equal(3, result.IndividualResults.Count);
            Assert.All(result.IndividualResults, r => Assert.True(r.Passed));
            var agents = factory.Created.Select(r => r.Agent).ToArray();
            Assert.Equal(3, agents.Length);
            Assert.Equal(3, agents.Distinct().Count());
            Assert.Equal(3, agents.Select(a => a.Conversation).Distinct().Count());

            // Fresh conversations: each discovered the card itself, sent without a context, and ran its turn 0.
            Assert.Equal(3, handler.CardRequestCount);
            var sends = handler.Requests.Where(r => r.JsonRpcMethod == "SendMessage").ToArray();
            Assert.Equal(3, sends.Length);
            Assert.All(sends, s => Assert.Null(LiveAgentRig.ContextIdOf(s)));
            Assert.All(factory.Created, rig => Assert.Equal(0, Assert.Single(rig.Turns).Identity.TurnIndex));
            Assert.Equal(3, factory.Created.Select(rig => Assert.Single(rig.Turns).Identity.RunId).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            foreach (var rig in factory.Created)
            {
                await rig.DisposeAsync();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // (g) cancellation during a turn
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunEvaluationAsync_CancelledDuringTheSend_RecordsTheTurnCanceledExceptionAsAnOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new MAFEvaluationHarness(verbose: false);
        var cancelOnSend = new CancelOnSendHandler(cancellation);
        await using var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture),
            wrap: fake =>
            {
                cancelOnSend.InnerHandler = fake;
                return cancelOnSend;
            });

        var result = await harness.RunEvaluationAsync(rig.Agent, StatusCase(), null, cancellation.Token);

        Assert.True(result.HasError);
        Assert.False(result.Passed);
        var canceled = Assert.IsType<WorkIqTurnCanceledException>(result.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(canceled);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(WorkIqTurnOutcome.Indeterminate, canceled.TurnResult.Outcome);   // a local stop is never a remote cancel
        Assert.Equal(WorkIqFailureCategory.Canceled, canceled.TurnResult.Failure?.Category);
        Assert.Same(canceled.TurnResult, Assert.Single(rig.Turns));
        Assert.Null(result.ActualOutput);
        Assert.Equal(1, cancelOnSend.Posts);                                        // the send only: no CancelTask, no resend
    }

    [Fact]
    public async Task RunBatchAsync_CancelledDuringTheFirstTurn_RethrowsAndReportsNoCompletedCase()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new MAFEvaluationHarness(verbose: false);
        var cancelOnSend = new CancelOnSendHandler(cancellation);
        await using var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture),
            wrap: fake =>
            {
                cancelOnSend.InnerHandler = fake;
                return cancelOnSend;
            });
        var observed = new List<TestResult>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunBatchAsync(rig.Agent, ThreeCases(), null, cancellation.Token, observed.Add));

        Assert.Empty(observed);                                  // an interrupted case is not a completed failing case
        var turn = Assert.Single(rig.Turns);                     // the sink still saw the interrupted turn
        Assert.Equal(WorkIqFailureCategory.Canceled, turn.Failure?.Category);
        Assert.Equal(1, cancelOnSend.Posts);                     // one send, nothing resent, no later case started
        Assert.Equal(1, rig.Agent.TurnsUsed);
    }

    // ---------------------------------------------------------------------------------------------------------
    // (h) the evidence travels on the response
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_CompletedTurn_EvidenceReadsBackFromAdditionalProperties()
    {
        await using var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture));
        rig.Agent.NextCaseId = "atlas-1";

        var response = await rig.Agent.InvokeAsync(Question);

        var evidence = response.Evidence();
        Assert.NotNull(evidence);
        Assert.Same(evidence, response.AdditionalProperties![Microsoft365Evidence.Key]);
        Assert.True(response.TryGetEvidence(out var again));
        Assert.Same(evidence, again);
        Assert.Same(evidence, Assert.Single(rig.Turns).Evidence);
        Assert.Equal(Microsoft365Evidence.SchemaVersion, evidence.SchemaVersion);
        Assert.Equal("atlas-1", evidence.Identity.CaseId);
        Assert.Equal("TASK_STATE_COMPLETED", evidence.TaskState);
        Assert.Equal(response.Text, evidence.Answer!.RawText.Value);
        Assert.Equal(2, evidence.CitationSources.Items.Length);
    }

    // ---------------------------------------------------------------------------------------------------------
    // (i) building is free
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BuildLive_BeforeTheFirstInvoke_MakesZeroRequests()
    {
        await using var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture));

        rig.Handler.AssertNoRequests();
        Assert.Equal(0, rig.Tokens.Calls);
        Assert.Empty(rig.Turns);

        // The fake is live, not dead: the first invoke reaches it (card + send), so the zero above is a real zero.
        await rig.Agent.InvokeAsync(Question);
        Assert.Equal(1, rig.Handler.CardRequestCount);
        Assert.Single(rig.Sends());
        Assert.True(rig.Tokens.Calls > 0);
    }

    /// <summary>An <see cref="IAgentFactory"/> that builds a fresh live agent (own conversation, own diagnostics) per call.</summary>
    private sealed class LiveAgentFactory(FakeWorkIqHandler handler) : IAgentFactory
    {
        private readonly List<LiveAgentRig> _created = [];

        public IReadOnlyList<LiveAgentRig> Created => _created;

        public string ModelId => "workiq-default";

        public string ModelName => "Work IQ default agent";

        public ModelConfiguration? Configuration => null;

        public IEvaluableAgent CreateAgent()
        {
            var rig = LiveAgentRig.Create(handler);
            _created.Add(rig);
            return rig.Agent;
        }
    }

    /// <summary>
    /// The innermost handler of a cancellation test: card GETs reach the fake; a POST (the send) is counted, cancels the
    /// caller's token and then waits for that cancellation like a slow service, so the reply is never served.
    /// </summary>
    private sealed class CancelOnSendHandler(CancellationTokenSource cancellation) : DelegatingHandler
    {
        private int _posts;

        /// <summary>How many POSTs (sends) reached this handler.</summary>
        public int Posts => Volatile.Read(ref _posts);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            Interlocked.Increment(ref _posts);
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay ends only by cancellation");
        }
    }
}
