// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Conversation;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Tests.Microsoft365.Agent;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Conversation;

/// <summary>
/// The seams the conversation's collaborators share: the turn context (failure, trace, capture and call helpers), the
/// conversation state (epochs), the turn tokens (the movable deadline), and the deferred argument check of the two
/// streamed operations. The end-to-end behaviour is pinned by <see cref="WorkIqConversationTests"/>.
/// </summary>
public sealed class ConversationSeamTests
{
    private static readonly WorkIqTurnRequest Request = new("Summarize the status of Project Atlas.", "case-1", "turn-1");

    private static TurnContext NewContext(int epoch = 0, WorkIqDiagnostics? diagnostics = null, string? captureScope = null) =>
        new(Request, TurnResults.Identity(Request), WorkIqOperation.Send, TurnResults.At, epoch, CancellationToken.None, diagnostics ?? WorkIqDiagnostics.None)
        {
            CaptureScope = captureScope,
        };

    [Fact]
    public void TurnContext_Trace_FillsTheCaseAndTurnIdsAndKeepsTheCallersIds()
    {
        var trace = new ListTraceSink();
        var diagnostics = new WorkIqDiagnostics("run-seam", null, [trace], [], new WaiterTrackingTimeProvider(TurnResults.At));
        var ctx = NewContext(diagnostics: diagnostics);

        ctx.Trace(WorkIqTraceStage.GetTask, "seam.check", "one event", WorkIqTraceLevel.Warning, taskId: "task-1", a2aState: "TASK_STATE_WORKING");

        var e = Assert.Single(trace.Events);
        Assert.Equal(WorkIqTraceStage.GetTask, e.Stage);
        Assert.Equal(WorkIqTraceLevel.Warning, e.Level);
        Assert.Equal("seam.check", e.Code);
        Assert.Equal("case-1", e.CaseId);
        Assert.Equal("turn-1", e.TurnId);
        Assert.Equal("task-1", e.TaskId);
        Assert.Null(e.ContextId);
        Assert.Equal("TASK_STATE_WORKING", e.A2AState);
    }

    [Fact]
    public void TurnContext_FailThenTerminalFailureThenRecovered_EachChangesOnlyItsOwnPart()
    {
        var ctx = NewContext();
        Assert.Null(ctx.Failure);
        Assert.Equal(TransportOutcome.Completed, ctx.Transport);

        var failure = new WorkIqFailure(WorkIqFailureCategory.Transport, "the connection dropped", OutcomeCertainty.Indeterminate, WorkIqOperation.Send);
        ctx.Fail(failure, TransportOutcome.Interrupted);
        Assert.Same(failure, ctx.Failure);
        Assert.Equal(TransportOutcome.Interrupted, ctx.Transport);

        ctx.SetTerminalFailure(null);
        Assert.Null(ctx.Failure);
        Assert.Equal(TransportOutcome.Interrupted, ctx.Transport);

        ctx.MarkRecovered();
        Assert.Null(ctx.Failure);
        Assert.Equal(TransportOutcome.Recovered, ctx.Transport);
    }

    [Fact]
    public void TurnContext_AddCapture_IgnoresNullKeepsOneCapturePerIdAndTracksTheLast()
    {
        var ctx = NewContext();
        ctx.AddCapture(null);
        Assert.Empty(ctx.Captures);
        Assert.Null(ctx.LastCapture);
        Assert.Equal("uncaptured", ctx.CaptureId);

        var first = new RawCaptureRef("cap-1", CaptureState.Complete, "policy-1", null, null, null);
        var sameId = first with { State = CaptureState.Truncated };
        ctx.AddCapture(first);
        ctx.AddCapture(sameId);

        Assert.Same(first, Assert.Single(ctx.Captures));
        Assert.Same(sameId, ctx.LastCapture);
        Assert.Equal("cap-1", ctx.CaptureId);
    }

    [Fact]
    public void TurnContext_Call_CarriesTheTurnsIdsAndCaptureFolder()
    {
        var ctx = NewContext(captureScope: "run-seam/turn-1");

        var call = ctx.Call("rpc-7");

        Assert.Equal("rpc-7", call.RpcId);
        Assert.Equal("run-seam/turn-1", call.CaptureScope);
        Assert.Equal("case-1", call.CaseId);
        Assert.Equal("turn-1", call.TurnId);
        Assert.True(call.CallerRegistersIssues);
        Assert.Null(ctx.Call().RpcId);
        Assert.NotSame(call, ctx.Call("rpc-7"));
    }

    [Fact]
    public void TurnContext_CaptureScopeFor_ReplacesPathSeparatorsAndNeverReturnsAnEmptySegment()
    {
        Assert.Equal("run_1/turn_2", TurnContext.CaptureScopeFor("run/1", "turn\\2"));
        Assert.Equal("_/_", TurnContext.CaptureScopeFor("", ""));
    }

    [Fact]
    public void ConversationState_ResetStartsANewEpoch_ATurnFromTheOldEpochCannotWriteItsContextBack()
    {
        var state = new ConversationState();
        var (index, epoch, context) = state.StartTurn();
        Assert.Equal(0, index);
        Assert.Equal(0, epoch);
        Assert.Null(context);

        var finished = NewContext(epoch);
        finished.ContextId = "ctx-1";
        var stale = NewContext(epoch);
        stale.ContextId = "ctx-stale";

        state.RecordTurn(finished, TurnResults.Completed(Request));
        Assert.Equal("ctx-1", state.ContextId);
        Assert.Equal("ctx-1", state.ContextToContinue(ConversationPolicy.ContinueContext));
        Assert.Null(state.ContextToContinue(ConversationPolicy.FreshContextPerInvocation));
        Assert.Null(state.ActiveTask);
        var (peekContext, peekTask, peekOutcome, _) = state.Peek();
        Assert.Equal("ctx-1", peekContext);
        Assert.Equal(TurnResults.Handle, peekTask);
        Assert.Equal(WorkIqTurnOutcome.Completed, peekOutcome);

        Assert.Equal("ctx-1", state.Reset());
        state.RecordTurn(stale, TurnResults.Completed(Request));

        Assert.Null(state.ContextId);
        var (afterContext, afterTask, afterOutcome, afterCapture) = state.Peek();
        Assert.Null(afterContext);
        Assert.Null(afterTask);
        Assert.Null(afterOutcome);
        Assert.Null(afterCapture);
        var (nextIndex, nextEpoch, nextContext) = state.StartTurn();
        Assert.Equal(1, nextIndex);
        Assert.Equal(1, nextEpoch);
        Assert.Null(nextContext);
    }

    [Fact]
    public void TurnTokens_MoveDeadline_ExtendOnlyNeverShortensAndAMovedDeadlineStillFires()
    {
        var clock = new WaiterTrackingTimeProvider(TurnResults.At);
        var now = clock.GetUtcNow();
        using var tokens = TurnTokens.Create(CancellationToken.None, CancellationToken.None, TimeSpan.FromSeconds(10), clock);
        Assert.Equal(now + TimeSpan.FromSeconds(10), tokens.DeadlineDue);

        Assert.False(tokens.MoveDeadline(now + TimeSpan.FromSeconds(5), now, extendOnly: true));
        Assert.Equal(now + TimeSpan.FromSeconds(10), tokens.DeadlineDue);
        Assert.True(tokens.MoveDeadline(now + TimeSpan.FromSeconds(30), now, extendOnly: true));
        Assert.Equal(now + TimeSpan.FromSeconds(30), tokens.DeadlineDue);
        Assert.True(tokens.MoveDeadline(now + TimeSpan.FromSeconds(5), now, extendOnly: false));
        Assert.Equal(now + TimeSpan.FromSeconds(5), tokens.DeadlineDue);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.True(tokens.DeadlineFired);
        Assert.True(tokens.Token.IsCancellationRequested);
        Assert.False(tokens.Caller.IsCancellationRequested);
        Assert.False(tokens.MoveDeadline(clock.GetUtcNow() + TimeSpan.FromSeconds(60), clock.GetUtcNow(), extendOnly: true));
    }

    [Fact]
    public void TurnTokens_WithoutADeadline_NothingMovesAndTheCallerIsKeptApart()
    {
        var clock = new WaiterTrackingTimeProvider(TurnResults.At);
        var now = clock.GetUtcNow();
        using var caller = new CancellationTokenSource();
        using var tokens = TurnTokens.Create(caller.Token, CancellationToken.None, TimeSpan.Zero, clock);

        Assert.Null(tokens.DeadlineDue);
        Assert.False(tokens.MoveDeadline(now + TimeSpan.FromSeconds(1), now, extendOnly: false));

        caller.Cancel();

        Assert.True(tokens.Token.IsCancellationRequested);
        Assert.True(tokens.Caller.IsCancellationRequested);
        Assert.False(tokens.Abandon.IsCancellationRequested);
        Assert.False(tokens.DeadlineFired);
    }

    [Fact]
    public async Task StreamTurnAsyncAndSubscribeAsync_NullArgument_ThrowOnlyWhenTheEnumerationStarts()
    {
        await using var conversation = new WorkIqConversation(new FakeWorkIqA2AClient(), WorkIqClientHarness.DefaultConfig(), new WorkIqOptions(), TimeProvider.System);

        // Getting the sequence runs nothing, as with any async iterator.
        var stream = conversation.StreamTurnAsync(null!);
        var subscription = conversation.SubscribeAsync(null!);

        var streamError = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var unused in stream)
            {
            }
        });
        var subscribeError = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var unused in subscription)
            {
            }
        });

        Assert.Equal("request", streamError.ParamName);
        Assert.Equal("task", subscribeError.ParamName);
        Assert.False(conversation.IsTurnActive);
    }
}
