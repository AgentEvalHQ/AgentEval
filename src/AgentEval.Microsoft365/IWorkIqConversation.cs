// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365;

/// <summary>
/// The rich API over one Work IQ conversation: one instance = one server context, one active turn at a time.
/// <see cref="RunTurnAsync"/> and <see cref="StreamTurnAsync"/> are alternative ways to submit <b>one</b> turn
/// (calling both submits twice); <see cref="SubscribeAsync"/> watches an existing task and never resubmits input.
/// A second concurrent turn or subscription is rejected with <see cref="InvalidOperationException"/>. A send is
/// never repeated automatically; recovery goes through the task id. This is also the public test seam:
/// <see cref="Microsoft365AgentFactory.FromConversation"/> wraps any implementation in a
/// <see cref="WorkIqEvaluableAgent"/>.
/// </summary>
public interface IWorkIqConversation : IAsyncDisposable
{
    /// <summary>The server context id of the current conversation; null before the first reply or after a reset.</summary>
    string? ContextId { get; }

    /// <summary>The task of the active or last turn while it is still addressable (working or suspended); null otherwise.</summary>
    WorkIqTaskHandle? ActiveTask { get; }

    /// <summary><see langword="true"/> while a turn or subscription owns the conversation.</summary>
    bool IsTurnActive { get; }

    /// <summary>
    /// Submits one turn as a blocking <c>SendMessage</c> (<c>returnImmediately: false</c>), reconciles a still-active
    /// task with bounded <c>GetTask</c> polling, and returns the rich result for every outcome: a transport failure,
    /// a refused card or a run-out turn deadline is a result (<see cref="WorkIqTurnOutcome.Failed"/> or
    /// <see cref="WorkIqTurnOutcome.Indeterminate"/>), never an exception.
    /// </summary>
    /// <param name="request">The turn.</param>
    /// <param name="cancellationToken">Stops local waiting; never cancels the remote task.</param>
    /// <exception cref="InvalidOperationException">A turn or subscription is already active on this conversation.</exception>
    /// <exception cref="WorkIqTurnCanceledException">
    /// <paramref name="cancellationToken"/> or <see cref="AbandonAndResetAsync"/> stopped the turn. It is an
    /// <see cref="OperationCanceledException"/> that carries the result observed until the stop
    /// (<see cref="IWorkIqTurnResultCarrier.TurnResult"/>).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The conversation was disposed.</exception>
    Task<WorkIqTurnResult> RunTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one turn as <c>SendStreamingMessage</c> and yields the rich events as they are reduced, ending with
    /// <see cref="WorkIqStreamEvent.TurnFinished"/>. Shares reduction, answer selection and normalisation with
    /// <see cref="RunTurnAsync"/>. On EOF without a terminal state and a known task id, reconciles through bounded
    /// <c>GetTask</c> polling under the recovery budget. The enumeration throws what <see cref="RunTurnAsync"/> throws.
    /// </summary>
    /// <param name="request">The turn.</param>
    /// <param name="cancellationToken">Stops local waiting; never cancels the remote task.</param>
    IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current snapshot of a task created by this conversation (same endpoint and principal), without
    /// resubmitting anything. Allowed while a turn is active: the read neither waits for the turn nor shares its
    /// request id or raw capture.
    /// </summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The handle belongs to another endpoint or principal.</exception>
    /// <exception cref="WorkIqProtocolException">Discovery or the read failed (a stop of <paramref name="cancellationToken"/> included); no turn result exists here.</exception>
    /// <exception cref="ObjectDisposedException">The conversation was disposed.</exception>
    Task<WorkIqTaskSnapshot> GetTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests remote cancellation of a task and returns the snapshot the service replied with. Best effort: the
    /// task is canceled only if the returned state says so (it may already be completed, not cancelable or not found).
    /// Allowed while a turn is active, like <see cref="GetTaskAsync"/>.
    /// </summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Cancellation token for the cancel request itself; do not pass an already-canceled token.</param>
    /// <exception cref="InvalidOperationException">The handle belongs to another endpoint or principal.</exception>
    /// <exception cref="WorkIqProtocolException">Discovery or the request failed (a task not found included); no turn result exists here.</exception>
    /// <exception cref="ObjectDisposedException">The conversation was disposed.</exception>
    Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches to an existing task via <c>SubscribeToTask</c>: the first frame is a snapshot that replaces local
    /// state, then future events follow. Never resubmits input. Rejected while a turn is active. The enumeration throws
    /// what <see cref="RunTurnAsync"/> throws, and <see cref="InvalidOperationException"/> for a handle of another
    /// endpoint or principal.
    /// </summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Stops local waiting.</param>
    IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Not supported in this preview: throws <see cref="NotSupportedException"/> for any non-null arguments and sends
    /// nothing. A paused task (input or a confirmation required) keeps its pause on
    /// <see cref="WorkIqTurnResult.PendingInteraction"/>; nothing is ever approved automatically. Reserved for continuing
    /// such a task with the host's explicit decision.
    /// </summary>
    /// <param name="interaction">The pending interaction.</param>
    /// <param name="response">The host's response.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interaction"/> or <paramref name="response"/> is null.</exception>
    /// <exception cref="NotSupportedException">Always, once the arguments are not null.</exception>
    Task<WorkIqTurnResult> ContinueAsync(WorkIqPendingInteraction interaction, WorkIqInteractionResponse response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the local context so the next turn starts a fresh server context. Local only: it does not cancel,
    /// delete or erase any remote task or conversation. Rejected with <see cref="InvalidOperationException"/>
    /// while a turn is active — use <see cref="AbandonAndResetAsync"/> for that.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly abandons local ownership of the active operation: stops local waiting, waits for the reducer to
    /// quiesce within the recovery budget, preserves the last known/indeterminate outcome, then clears the local
    /// context. It never sends <c>CancelTask</c>; if the operation cannot quiesce the receipt says so and no new
    /// turn may start on this context.
    /// </summary>
    /// <param name="reason">The caller's reason (recorded; user content).</param>
    /// <param name="cancellationToken">Cancellation token for the abandonment itself.</param>
    Task<WorkIqAbandonmentReceipt> AbandonAndResetAsync(string reason, CancellationToken cancellationToken = default);
}
