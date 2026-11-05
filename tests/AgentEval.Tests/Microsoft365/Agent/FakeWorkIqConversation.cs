// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Tests.Microsoft365.Agent;

/// <summary>Hand-built turn results for the agent tests (no transport, no normaliser): every evidence channel is <c>NotRequested</c>.</summary>
internal static class TurnResults
{
    public static readonly DateTimeOffset At = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    public static readonly WorkIqTaskHandle Handle = new(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, "task-fake-1", "ctx-fake-1", null, "principal-fake");

    public static TurnIdentity Identity(WorkIqTurnRequest request, string? taskId = "task-fake-1") =>
        new("run-fake", request.CaseId, request.TurnId, 0, 0, "rpc-fake-1", "msg-client-1", "msg-agent-1", taskId, "ctx-fake-1");

    public static Microsoft365TurnEvidence Evidence(TurnIdentity identity, string? taskState) => new(
        Microsoft365Evidence.SchemaVersion,
        Microsoft365Evidence.NormalizerVersion,
        Microsoft365Evidence.ProviderProfileVersion,
        Microsoft365Evidence.A2AVersion,
        identity,
        taskState,
        CaptureState.NotRetained,
        null,
        EvidenceSet<ToolSelectionObservation>.NotRequested(EvidenceScope.Turn),
        EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Turn),
        EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Turn),
        EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Turn),
        EvidenceSet<CitationMarker>.NotRequested(EvidenceScope.Turn),
        EvidenceSet<CitationSource>.NotRequested(EvidenceScope.Turn),
        ImmutableArray<CitationLink>.Empty,
        EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Turn),
        ImmutableArray<RawCaptureRef>.Empty,
        ImmutableArray<EvidenceIssue>.Empty);

    public static WorkIqTurnTiming Timing(int recoveryRequests = 0) => new(At, At.AddMilliseconds(200), At.AddMilliseconds(400), At.AddMilliseconds(400), At.AddMilliseconds(500), recoveryRequests);

    public static WorkIqTurnResult Completed(WorkIqTurnRequest request, string text = "The pilot starts on 17 November.")
    {
        var identity = Identity(request);
        return new WorkIqTurnResult(
            WorkIqTurnOutcome.Completed,
            TransportOutcome.Completed,
            OutputCompleteness.Complete,
            TelemetryCompleteness.NotRequested,
            Handle,
            "ctx-fake-1",
            text,
            Evidence(identity, "TASK_STATE_COMPLETED"),
            null,
            null,
            Timing(),
            identity);
    }

    public static WorkIqTurnResult NotCompleted(WorkIqTurnRequest request, WorkIqTurnOutcome outcome, WorkIqFailureCategory? failure = null, bool withTask = true)
    {
        if (outcome == WorkIqTurnOutcome.Completed)
        {
            throw new ArgumentException("Use Completed().", nameof(outcome));
        }

        var identity = Identity(request, withTask ? "task-fake-1" : null);
        var suspended = outcome is WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired;
        var pending = suspended
            ? new WorkIqPendingInteraction(Handle, outcome == WorkIqTurnOutcome.AuthRequired ? WorkIqInteractionKind.AuthenticationRequired : WorkIqInteractionKind.ConfirmationRequired, "req-fake-1", null, [], ["Allow", "Cancel"], "workiq-interaction/v1", null, [])
            : null;
        var category = failure ?? outcome switch
        {
            WorkIqTurnOutcome.Indeterminate => WorkIqFailureCategory.Timeout,
            WorkIqTurnOutcome.Canceled => WorkIqFailureCategory.RemoteError,
            _ => WorkIqFailureCategory.RemoteError,
        };
        return new WorkIqTurnResult(
            outcome,
            outcome == WorkIqTurnOutcome.Indeterminate ? TransportOutcome.Interrupted : TransportOutcome.Completed,
            OutputCompleteness.None,
            TelemetryCompleteness.NotRequested,
            withTask ? Handle : null,
            withTask ? "ctx-fake-1" : null,
            null,
            Evidence(identity, withTask ? "TASK_STATE_WORKING" : null),
            pending,
            suspended ? null : new WorkIqFailure(category, $"fake {outcome}", category is WorkIqFailureCategory.Timeout or WorkIqFailureCategory.Canceled ? OutcomeCertainty.Indeterminate : OutcomeCertainty.Known, WorkIqOperation.Send, TaskId: withTask ? Handle.TaskId : null),
            Timing(),
            identity);
    }
}

/// <summary>A scripted <see cref="IWorkIqConversation"/>: records every turn request and reset, replies through <see cref="OnRun"/>.</summary>
internal sealed class FakeWorkIqConversation : IWorkIqConversation
{
    private int _resets;
    private int _disposals;

    public Func<WorkIqTurnRequest, CancellationToken, Task<WorkIqTurnResult>> OnRun { get; set; } =
        (request, _) => Task.FromResult(TurnResults.Completed(request));

    public List<WorkIqTurnRequest> Turns { get; } = [];

    public List<string> Calls { get; } = [];

    public int Resets => Volatile.Read(ref _resets);

    public int Disposals => Volatile.Read(ref _disposals);

    public string? ContextId { get; set; }

    public WorkIqTaskHandle? ActiveTask { get; set; }

    public bool IsTurnActive { get; set; }

    public Task<WorkIqTurnResult> RunTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default)
    {
        Turns.Add(request);
        Calls.Add("run");
        return OnRun(request, cancellationToken);
    }

    public IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorkIqTaskSnapshot> GetTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<WorkIqTurnResult> ContinueAsync(WorkIqPendingInteraction interaction, WorkIqInteractionResponse response, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _resets);
        Calls.Add("reset");
        ContextId = null;
        ActiveTask = null;
        return Task.CompletedTask;
    }

    public Task<WorkIqAbandonmentReceipt> AbandonAndResetAsync(string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkIqAbandonmentReceipt(reason, TurnResults.At, true, ContextId, ActiveTask, null, null, false));

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        return ValueTask.CompletedTask;
    }
}
