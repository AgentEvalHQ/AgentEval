// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Tests.Microsoft365.Auth;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Agent;

/// <summary>
/// <see cref="WorkIqEvaluableAgent"/> over a <see cref="FakeWorkIqConversation"/> (completion gate, typed
/// exceptions, the turn budget, the conversation policy, the sink order) and <see cref="Microsoft365AgentFactory"/>
/// (consent gate, no network on build, the full live wiring through the fake HTTP handler).
/// </summary>
public sealed class WorkIqEvaluableAgentTests
{
    private static (FakeWorkIqConversation Conversation, WorkIqEvaluableAgent Agent, List<WorkIqTurnResult> Sink, ListTraceSink Trace) Build(WorkIqOptions? options = null)
    {
        var conversation = new FakeWorkIqConversation();
        var sink = new List<WorkIqTurnResult>();
        var trace = new ListTraceSink();
        var diagnostics = new WorkIqDiagnostics("run-agent", null, [trace], [new ListIssueSink()]);
        var agent = new WorkIqEvaluableAgent(conversation, "Fake Work IQ", options, sink.Add, diagnostics);
        return (conversation, agent, sink, trace);
    }

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "agenteval-m365-tests", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Completion gate
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_Completed_ReturnsResponseWithEvidenceUnderTheKey()
    {
        var (conversation, agent, sink, _) = Build();
        await using (agent)
        {
            var response = await agent.InvokeAsync("Summarise the plan.");

            Assert.Equal("The pilot starts on 17 November.", response.Text);
            Assert.Equal(WorkIqEvaluableAgent.CompletedFinishReason, response.FinishReason);
            Assert.Null(response.RawMessages);
            Assert.Null(response.TokenUsage);
            Assert.Null(response.ModelId);
            Assert.NotNull(response.AdditionalProperties);
            var evidence = Assert.IsType<Microsoft365TurnEvidence>(response.AdditionalProperties[Microsoft365Evidence.Key]);
            Assert.Same(evidence, response.Evidence());
            Assert.True(response.TryGetEvidence(out var again));
            Assert.Same(evidence, again);
            Assert.Equal(1, agent.TurnsUsed);
            var result = Assert.Single(sink);
            Assert.Same(evidence, result.Evidence);
            var request = Assert.Single(conversation.Turns);
            Assert.Equal("Summarise the plan.", request.Input);
            Assert.Equal("case-1", request.CaseId);
            Assert.False(string.IsNullOrWhiteSpace(request.TurnId));
        }
    }

    [Fact]
    public async Task InvokeAsync_CompletedWithNullText_EmptyString()
    {
        var (conversation, agent, _, _) = Build();
        conversation.OnRun = (request, _) => Task.FromResult(TurnResults.Completed(request) with { AnswerText = null, AnswerCompleteness = OutputCompleteness.Empty });
        await using (agent)
        {
            var response = await agent.InvokeAsync("x");

            Assert.Equal("", response.Text);
        }
    }

    [Theory]
    [InlineData(OutputCompleteness.Ambiguous)]
    [InlineData(OutputCompleteness.None)]
    public async Task InvokeAsync_CompletedWithNoSelectedAnswer_MarkedCompletedNoAnswerWithTheFlag(OutputCompleteness completeness)
    {
        var (conversation, agent, sink, trace) = Build();
        conversation.OnRun = (request, _) => Task.FromResult(TurnResults.Completed(request) with { AnswerText = null, AnswerCompleteness = completeness });
        await using (agent)
        {
            var response = await agent.InvokeAsync("x");

            // A missing answer is never indistinguishable from an empty one.
            Assert.Equal("", response.Text);
            Assert.Equal(WorkIqEvaluableAgent.CompletedNoAnswerFinishReason, response.FinishReason);
            Assert.Equal("completed-no-answer", response.FinishReason);
            Assert.True(response.AdditionalProperties![Microsoft365Evidence.NoAnswerSelectedKey] is true);
            Assert.Equal(completeness.ToString(), response.AdditionalProperties[Microsoft365Evidence.AnswerCompletenessKey]);
            Assert.NotNull(response.Evidence());
            Assert.Single(sink);
            Assert.Contains(trace.Events, e => e.Code == "agent.turn_mapped" && e.Level == WorkIqTraceLevel.Warning && e.Message.Contains("NO selected answer", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("The pilot starts on 17 November.", OutputCompleteness.Complete)]
    [InlineData("", OutputCompleteness.Empty)]
    public async Task InvokeAsync_CompletedWithASelectedAnswer_FinishReasonCompletedAndFlagFalse(string text, OutputCompleteness completeness)
    {
        var (conversation, agent, _, _) = Build();
        conversation.OnRun = (request, _) => Task.FromResult(TurnResults.Completed(request, text) with { AnswerCompleteness = completeness });
        await using (agent)
        {
            var response = await agent.InvokeAsync("x");

            Assert.Equal(text, response.Text);
            Assert.Equal(WorkIqEvaluableAgent.CompletedFinishReason, response.FinishReason);
            Assert.True(response.AdditionalProperties![Microsoft365Evidence.NoAnswerSelectedKey] is false);
            Assert.Equal(completeness.ToString(), response.AdditionalProperties[Microsoft365Evidence.AnswerCompletenessKey]);
        }
    }

    [Theory]
    [InlineData(WorkIqTurnOutcome.InputRequired)]
    [InlineData(WorkIqTurnOutcome.AuthRequired)]
    [InlineData(WorkIqTurnOutcome.Failed)]
    [InlineData(WorkIqTurnOutcome.Rejected)]
    [InlineData(WorkIqTurnOutcome.Canceled)]
    [InlineData(WorkIqTurnOutcome.Indeterminate)]
    public async Task InvokeAsync_NonCompletedOutcome_ThrowsNotCompletedCarryingTheResult(WorkIqTurnOutcome outcome)
    {
        var (conversation, agent, sink, _) = Build();
        WorkIqTurnResult? produced = null;
        conversation.OnRun = (request, _) => Task.FromResult(produced = TurnResults.NotCompleted(request, outcome));
        await using (agent)
        {
            var ex = await Assert.ThrowsAsync<WorkIqTurnNotCompletedException>(() => agent.InvokeAsync("do it"));

            Assert.Same(produced, ex.TurnResult);
            Assert.IsAssignableFrom<IWorkIqTurnResultCarrier>(ex);
            Assert.Contains($"outcome={outcome}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("task=task-fake-1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("context=ctx-fake-1", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("do it", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, agent.TurnsUsed);
            Assert.Same(produced, Assert.Single(sink));
        }
    }

    [Fact]
    public async Task InvokeAsync_CallerTokenFiredWithCanceledResult_ThrowsCanceledException()
    {
        var (conversation, agent, sink, _) = Build();
        using var cts = new CancellationTokenSource();
        conversation.OnRun = (request, token) =>
        {
            cts.Cancel();
            return Task.FromResult(TurnResults.NotCompleted(request, WorkIqTurnOutcome.Indeterminate, WorkIqFailureCategory.Canceled, withTask: false));
        };
        await using (agent)
        {
            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => agent.InvokeAsync("x", cts.Token));

            Assert.IsAssignableFrom<OperationCanceledException>(ex);
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.Equal(WorkIqTurnOutcome.Indeterminate, ex.TurnResult.Outcome);
            Assert.Single(sink);
            Assert.Equal(1, agent.TurnsUsed);
        }
    }

    [Fact]
    public async Task InvokeAsync_ConversationThrowsCanceledException_PropagatesAfterSink()
    {
        var (conversation, agent, sink, trace) = Build();
        using var cts = new CancellationTokenSource();
        conversation.OnRun = (request, _) =>
        {
            cts.Cancel();
            throw new WorkIqTurnCanceledException(TurnResults.NotCompleted(request, WorkIqTurnOutcome.Indeterminate, WorkIqFailureCategory.Canceled), cts.Token);
        };
        await using (agent)
        {
            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => agent.InvokeAsync("x", cts.Token));

            Assert.Same(ex.TurnResult, Assert.Single(sink));
            Assert.Equal(1, agent.TurnsUsed);
            Assert.Contains(trace.Events, e => e.Code == "agent.turn_mapped" && e.Level == WorkIqTraceLevel.Warning);
        }
    }

    [Fact]
    public async Task InvokeAsync_SinkInvokedBeforeTheThrow()
    {
        var conversation = new FakeWorkIqConversation();
        var order = new List<string>();
        conversation.OnRun = (request, _) => Task.FromResult(TurnResults.NotCompleted(request, WorkIqTurnOutcome.Failed));
        var agent = new WorkIqEvaluableAgent(conversation, "ordered", onTurn: _ => order.Add("sink"));
        await using (agent)
        {
            try
            {
                await agent.InvokeAsync("x");
            }
            catch (WorkIqTurnNotCompletedException)
            {
                order.Add("throw");
            }

            Assert.Equal(new[] { "sink", "throw" }, order);
            Assert.True(agent.HasTurnSink);
        }
    }

    [Fact]
    public async Task InvokeAsync_SinkThrows_TracedAndPropagated()
    {
        var conversation = new FakeWorkIqConversation();
        var trace = new ListTraceSink();
        var diagnostics = new WorkIqDiagnostics("run-sink", null, [trace], [new ListIssueSink()]);
        var agent = new WorkIqEvaluableAgent(conversation, "sink", onTurn: _ => throw new IOException("disk full"), diagnostics: diagnostics);
        await using (agent)
        {
            await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("x"));

            Assert.Contains(trace.Events, e => e.Code == "agent.sink_failed" && e.Level == WorkIqTraceLevel.Error);
        }
    }

    [Fact]
    public async Task InvokeAsync_ProtocolExceptionFromConversation_PropagatesWithoutCounting()
    {
        var (conversation, agent, sink, _) = Build();
        conversation.OnRun = (_, _) => throw new WorkIqProtocolException(new WorkIqFailure(WorkIqFailureCategory.UntrustedHost, "refused", OutcomeCertainty.Known, WorkIqOperation.Discover));
        await using (agent)
        {
            await Assert.ThrowsAsync<WorkIqProtocolException>(() => agent.InvokeAsync("x"));

            Assert.Equal(0, agent.TurnsUsed);
            Assert.Empty(sink);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Budget, policy, case ids
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_MaxTurnsTwo_ThirdCallThrowsFatalBeforeAnySend()
    {
        var (conversation, agent, _, trace) = Build(new WorkIqOptions { MaxTurns = 2 });
        await using (agent)
        {
            await agent.InvokeAsync("one");
            await agent.InvokeAsync("two");

            var ex = await Assert.ThrowsAsync<WorkIqTurnBudgetExceededException>(() => agent.InvokeAsync("three"));

            Assert.IsAssignableFrom<FatalEvaluationException>(ex);
            Assert.Equal(2, ex.TurnsUsed);
            Assert.Equal(2, ex.MaxTurns);
            Assert.Equal(2, conversation.Turns.Count);
            Assert.Equal(2, agent.TurnsUsed);
            Assert.Contains("MaxTurns (2)", ex.Message, StringComparison.Ordinal);
            Assert.Contains(trace.Events, e => e.Code == "budget.exceeded" && e.Level == WorkIqTraceLevel.Error);
            Assert.Contains(trace.Events, e => e.Code == "issue.budget");
        }
    }

    [Fact]
    public async Task InvokeAsync_NonCompletedTurnsCountTowardTheBudget()
    {
        var (conversation, agent, _, _) = Build(new WorkIqOptions { MaxTurns = 1 });
        conversation.OnRun = (request, _) => Task.FromResult(TurnResults.NotCompleted(request, WorkIqTurnOutcome.Failed));
        await using (agent)
        {
            await Assert.ThrowsAsync<WorkIqTurnNotCompletedException>(() => agent.InvokeAsync("one"));
            await Assert.ThrowsAsync<WorkIqTurnBudgetExceededException>(() => agent.InvokeAsync("two"));

            Assert.Single(conversation.Turns);
        }
    }

    [Fact]
    public async Task InvokeAsync_MaxTurnsZero_NoCap()
    {
        var (conversation, agent, _, _) = Build(new WorkIqOptions { MaxTurns = 0 });
        await using (agent)
        {
            for (var i = 0; i < 5; i++)
            {
                await agent.InvokeAsync("again");
            }

            Assert.Equal(5, conversation.Turns.Count);
        }
    }

    [Fact]
    public async Task InvokeAsync_FreshContextPolicy_ResetsBeforeEveryTurn()
    {
        var (conversation, agent, _, _) = Build();
        await using (agent)
        {
            await agent.InvokeAsync("one");
            await agent.InvokeAsync("two");

            Assert.Equal(2, conversation.Resets);
            Assert.Equal(new[] { "reset", "run", "reset", "run" }, conversation.Calls);
        }
    }

    [Fact]
    public async Task InvokeAsync_ContinueContextPolicy_NeverResets()
    {
        var (conversation, agent, _, _) = Build(new WorkIqOptions { ConversationPolicy = ConversationPolicy.ContinueContext });
        await using (agent)
        {
            await agent.InvokeAsync("one");
            await agent.InvokeAsync("two");

            Assert.Equal(0, conversation.Resets);
            Assert.Equal(new[] { "run", "run" }, conversation.Calls);
        }
    }

    [Fact]
    public async Task InvokeAsync_NextCaseId_OneShotThenCounter()
    {
        var (conversation, agent, _, _) = Build();
        await using (agent)
        {
            agent.NextCaseId = "dataset-case-42";
            await agent.InvokeAsync("one");
            Assert.Null(agent.NextCaseId);
            await agent.InvokeAsync("two");
            await agent.InvokeAsync("three");

            Assert.Equal(new[] { "dataset-case-42", "case-1", "case-2" }, conversation.Turns.Select(t => t.CaseId).ToArray());
            Assert.Equal(3, conversation.Turns.Select(t => t.TurnId).Distinct().Count());
        }
    }

    [Fact]
    public async Task InvokeAsync_NullPrompt_Throws()
    {
        var (_, agent, _, _) = Build();
        await using (agent)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => agent.InvokeAsync(null!));
        }
    }

    [Fact]
    public async Task ResetSessionAsync_DelegatesToTheConversation()
    {
        var (conversation, agent, _, _) = Build();
        await using (agent)
        {
            await agent.ResetSessionAsync();

            Assert.Equal(1, conversation.Resets);
        }
    }

    [Fact]
    public async Task DisposeAsync_DisposesConversationAndOwnedOnceAndRejectsFurtherCalls()
    {
        var (conversation, agent, _, _) = Build();
        var owned = new OwnedProbe();
        agent.Owned.Add(owned);

        await agent.DisposeAsync();
        await agent.DisposeAsync();

        Assert.Equal(1, conversation.Disposals);
        Assert.Equal(1, owned.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("x"));
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkIqEvaluableAgent(null!, "n"));
        Assert.Throws<ArgumentException>(() => new WorkIqEvaluableAgent(new FakeWorkIqConversation(), " "));
        Assert.Throws<InvalidOperationException>(() => new WorkIqEvaluableAgent(new FakeWorkIqConversation(), "n", new WorkIqOptions { MaxTurns = -1 }));
        var agent = new WorkIqEvaluableAgent(new FakeWorkIqConversation(), "n");
        Assert.False(agent.HasTurnSink);
        Assert.Same(WorkIqDiagnostics.None, agent.Diagnostics);
        Assert.Equal("n", agent.Name);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Factory
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildLive_WithoutConsent_ThrowsBeforeAnyConstruction()
    {
        var handler = new FakeWorkIqHandler();
        var tokens = new StaticTokenProvider();
        var directory = TempDirectory();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Microsoft365AgentFactory.BuildLive(
                WorkIqClientHarness.DefaultConfig(),
                iUnderstandLiveSideEffects: false,
                new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(directory) },
                tokens,
                innerHandler: handler));

            Assert.Contains("nothing was sent", ex.Message, StringComparison.Ordinal);
            handler.AssertNoRequests();
            Assert.Equal(0, tokens.Calls);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void BuildLive_InvalidConfig_ThrowsBeforeAnyConstruction()
    {
        var handler = new FakeWorkIqHandler();
        var directory = TempDirectory();
        try
        {
            Assert.Throws<InvalidOperationException>(() => Microsoft365AgentFactory.BuildLive(
                new Microsoft365Config { TenantId = "", AppClientId = "" },
                iUnderstandLiveSideEffects: true,
                new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(directory) },
                new StaticTokenProvider(),
                innerHandler: handler));

            handler.AssertNoRequests();
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task BuildLive_WithConsent_MakesNoNetworkCallAndWiresTheLiveConversation()
    {
        var handler = new FakeWorkIqHandler();
        var tokens = new StaticTokenProvider();
        var directory = TempDirectory();
        try
        {
            var config = WorkIqClientHarness.DefaultConfig();
            var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(directory), RunId = "run-buildlive", MaxTurns = 3 };

            var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options, tokens, innerHandler: handler);
            await using (agent)
            {
                handler.AssertNoRequests();
                Assert.Equal(0, tokens.Calls);
                Assert.Equal(Microsoft365Config.DefaultDisplayName, agent.Name);
                Assert.Same(options, agent.Options);
                var conversation = Assert.IsType<WorkIqConversation>(agent.Conversation);
                Assert.Null(conversation.ContextId);
                Assert.False(conversation.IsTurnActive);
                Assert.Null(conversation.Card);
                Assert.Equal("run-buildlive", agent.Diagnostics.RunId);
                Assert.False(agent.Diagnostics.IsNone);
                Assert.Equal(2, agent.Owned.Count);
                Assert.Contains(agent.Diagnostics.RecentEvents, e => e.Code == WorkIqDiagnosticsFactory.ReadyEventCode);
                Assert.Contains(agent.Diagnostics.RecentEvents, e => e.Code == "config.agent_built" && e.Message.Contains("no network call", StringComparison.Ordinal));
            }

            Assert.True(File.Exists(Path.Combine(directory, "m365-trace-run-buildlive.jsonl")));
            handler.AssertNoRequests();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task BuildLive_EndToEndThroughTheFakeHandler_DirectMessageBecomesAResponse()
    {
        var handler = new FakeWorkIqHandler()
            .OnCard("cards/default-card-two-interfaces.json")
            .OnRpc("SendMessage", "envelopes/send-direct-message.json");
        var tokens = new StaticTokenProvider();
        var directory = TempDirectory();
        var sink = new List<WorkIqTurnResult>();
        try
        {
            var options = new WorkIqOptions { DiagnosticsDirectory = new DirectoryInfo(directory), RunId = "run-e2e" };
            var agent = Microsoft365AgentFactory.BuildLive(WorkIqClientHarness.DefaultConfig(), iUnderstandLiveSideEffects: true, options, tokens, sink.Add, handler);
            await using (agent)
            {
                var response = await agent.InvokeAsync("What is my next meeting?");

                Assert.StartsWith("Your next meeting", response.Text, StringComparison.Ordinal);
                Assert.NotNull(response.Evidence());
                Assert.Equal(1, handler.CardRequestCount);
                Assert.Equal(1, handler.CountOf("SendMessage"));
                Assert.Equal(2, tokens.Calls);
                Assert.All(handler.Requests, r => Assert.Equal("Bearer", r.AuthorizationScheme));
                Assert.All(handler.Requests, r => Assert.Equal("1.0", r.Headers["A2A-Version"]));
                var result = Assert.Single(sink);
                Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
                Assert.Equal("run-e2e", result.Identity.RunId);
                Assert.Equal(1, agent.TurnsUsed);
                Assert.Contains(agent.Diagnostics.RecentEvents, e => e.Code == "turn.outcome");
                Assert.DoesNotContain(agent.Diagnostics.RecentEvents, e => e.Message.Contains("eyJ", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task FromConversation_FakeConversation_EndToEnd()
    {
        var conversation = new FakeWorkIqConversation();
        var sink = new List<WorkIqTurnResult>();

        var agent = Microsoft365AgentFactory.FromConversation(conversation, "fake", new WorkIqOptions { MaxTurns = 2 }, sink.Add);
        await using (agent)
        {
            var response = await agent.InvokeAsync("hello");

            Assert.Equal("fake", agent.Name);
            Assert.Equal("The pilot starts on 17 November.", response.Text);
            Assert.Single(sink);
            Assert.Same(WorkIqDiagnostics.None, agent.Diagnostics);
            Assert.Throws<ArgumentNullException>(() => Microsoft365AgentFactory.FromConversation(null!, "x"));
        }

        Assert.Equal(1, conversation.Disposals);
    }

    private sealed class OwnedProbe : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
