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
    /// task with bounded <c>GetTask</c> polling, and returns the rich result for every outcome — it does not throw
    /// for a non-completed turn. Throws <see cref="WorkIqProtocolException"/> only when the transport layer failed
    /// before any result could be built, <see cref="InvalidOperationException"/> when a turn is already active, and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires before a result exists.
    /// </summary>
    /// <param name="request">The turn.</param>
    /// <param name="cancellationToken">Stops local waiting; never cancels the remote task.</param>
    Task<WorkIqTurnResult> RunTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one turn as <c>SendStreamingMessage</c> and yields the rich events as they are reduced, ending with
    /// <see cref="WorkIqStreamEvent.TurnFinished"/>. Shares reduction, answer selection and normalisation with
    /// <see cref="RunTurnAsync"/>. On EOF without a terminal state and a known task id, reconciles via <c>GetTask</c>
    /// / <see cref="SubscribeAsync"/> under the recovery budget.
    /// </summary>
    /// <param name="request">The turn.</param>
    /// <param name="cancellationToken">Stops local waiting; never cancels the remote task.</param>
    IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads the current snapshot of a task created by this conversation (same endpoint and principal), without resubmitting anything.</summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WorkIqTaskSnapshot> GetTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests remote cancellation of a task and returns the snapshot the service replied with. Best effort: the
    /// task is canceled only if the returned state says so (it may already be completed, not cancelable or not found).
    /// </summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Cancellation token for the cancel request itself; do not pass an already-canceled token.</param>
    Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches to an existing task via <c>SubscribeToTask</c>: the first frame is a snapshot that replaces local
    /// state, then future events follow. Never resubmits input. Rejected while a turn is active.
    /// </summary>
    /// <param name="task">The task handle.</param>
    /// <param name="cancellationToken">Stops local waiting.</param>
    IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Continues a suspended task with the host's explicit decision or input (same task and context, new message
    /// id) and runs the continuation like a turn. Never called automatically by any evaluator.
    /// </summary>
    /// <param name="interaction">The pending interaction being answered.</param>
    /// <param name="response">The validated response.</param>
    /// <param name="cancellationToken">Stops local waiting.</param>
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
