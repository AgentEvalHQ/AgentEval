// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using AgentEval.Microsoft365.Conversation;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365;

/// <summary>
/// The production <see cref="IWorkIqConversation"/>. It owns the server <c>contextId</c> and the current task handle,
/// and it lets one turn run at a time. It runs the blocking turn (a send, then a bounded <c>GetTask</c>
/// reconciliation), the streamed turn and the subscription, reset and abandonment. Evidence is normalised exactly once
/// per turn. Card discovery runs once per conversation, and the card digest is recorded.
/// <see cref="Microsoft365AgentFactory.BuildLive"/> and the readiness check construct it. A host reaches it through
/// <see cref="IWorkIqConversation"/> (<see cref="WorkIqEvaluableAgent.Conversation"/>), and tests use that seam too.
/// </summary>
/// <remarks>
/// <para>
/// This class keeps the public API, the turn gate and the order of each turn's steps. Collaborators do the steps:
/// <see cref="CardDiscovery"/>, <see cref="TurnSender"/>, <see cref="TurnRecovery"/>, <see cref="StreamEventProjector"/>
/// and <see cref="TurnFinalizer"/>. <see cref="ConversationState"/> holds the state that outlives a turn, and
/// <see cref="TurnContext"/> holds what one turn accumulates.
/// </para>
/// <para>
/// Every turn yields a typed <see cref="WorkIqTurnResult"/>. A transport failure whose remote state is known (an HTTP
/// error reply, a JSON-RPC error, a refused card) ends as <see cref="WorkIqTurnOutcome.Failed"/>. One whose state is
/// unknown (connection loss, timeout, local stop before a task id) ends as <see cref="WorkIqTurnOutcome.Indeterminate"/>.
/// The input is never resent. A stop driven by the caller's token (or by <see cref="AbandonAndResetAsync"/>) throws
/// <see cref="WorkIqTurnCanceledException"/> carrying that result. A turn never throws
/// <see cref="WorkIqProtocolException"/>. The explicit task reads (<see cref="GetTaskAsync"/>,
/// <see cref="CancelTaskAsync"/>) do, for any discovery or transport failure, because no turn result exists there.
/// </para>
/// <para>
/// Issues: a turn that did not complete registers one issue for its outcome (<see cref="OutcomeIssueCode"/>, classified
/// by <see cref="WorkIqIssueClassifier"/>). Its calls to the client trace their failures without registering them again.
/// A turn that got no bearer token is the exception: the auth layer registered that failure already.
/// </para>
/// <para>
/// Zero-valued budgets: a zero <see cref="WorkIqOptions.TurnDeadline"/> means no deadline. A zero
/// <see cref="WorkIqOptions.RecoveryBudget"/> means no <c>GetTask</c> polling after a non-terminal reply; the turn is
/// then <see cref="WorkIqTurnOutcome.Indeterminate"/> with a timeout failure.
/// </para>
/// </remarks>
internal sealed class WorkIqConversation : IWorkIqConversation
{
    /// <summary>The versioned interaction profile that recognises Work IQ pauses (confirmation cards, auth-required payloads); the evidence decoder's id.</summary>
    public const string InteractionProfileId = InteractionDecoder.ProfileId;

    /// <summary>The key of the pause payload inside a status-message data part for <c>TASK_STATE_INPUT_REQUIRED</c>.</summary>
    public const string InputRequiredKey = InteractionDecoder.InputRequiredKey;

    /// <summary>The key of the pause payload inside a status-message data part for <c>TASK_STATE_AUTH_REQUIRED</c>.</summary>
    public const string AuthRequiredKey = InteractionDecoder.AuthRequiredKey;

    /// <summary>The message of the <see cref="NotSupportedException"/> <see cref="ContinueAsync"/> throws.</summary>
    internal const string ContinueNotSupportedMessage =
        "Continuing a paused task is not supported in this preview; nothing was sent. The pending interaction stays on the turn result (WorkIqTurnResult.PendingInteraction).";

    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConversationState _state = new();
    private readonly TaskHandleFactory _handles;
    private readonly TurnFailures _failures;
    private readonly CardDiscovery _discovery;
    private readonly TurnSender _sender;
    private readonly TurnRecovery _recovery;
    private readonly StreamEventProjector _projector;
    private readonly TurnFinalizer _finalizer;
    private CancellationTokenSource _abandonCts = new();
    private int _disposed;

    internal WorkIqConversation(IWorkIqA2AClient client, Microsoft365Config config, WorkIqOptions options, TimeProvider clock, WorkIqDiagnostics? diagnostics = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Diagnostics = diagnostics ?? WorkIqDiagnostics.None;
        config.Validate();
        options.Validate();
        Profile = options.ProfileOverride ?? config.ResolveProfile();

        _handles = new TaskHandleFactory(config, clock, Diagnostics);
        _failures = new TurnFailures(config, options);
        _discovery = new CardDiscovery(client, _failures);
        var observer = new TurnObserver(_handles, _state);
        var interactions = new PendingInteractionBuilder(options, Diagnostics);
        _sender = new TurnSender(client, config, options, Profile, _state, observer, _failures);
        _recovery = new TurnRecovery(client, options, clock, _state, observer, _failures);
        _projector = new StreamEventProjector(_handles, observer, _failures, interactions);
        _finalizer = new TurnFinalizer(clock, Profile, _state, interactions);
    }

    /// <summary>The diagnostics every turn of this conversation traces through (<see cref="WorkIqDiagnostics.None"/> when none was given).</summary>
    internal WorkIqDiagnostics Diagnostics { get; }

    /// <summary>The transport seam.</summary>
    internal IWorkIqA2AClient Client { get; }

    /// <summary>The validated connection settings.</summary>
    internal Microsoft365Config Config { get; }

    /// <summary>The validated runtime options.</summary>
    internal WorkIqOptions Options { get; }

    /// <summary>The clock used for every local timestamp (injectable for deterministic tests).</summary>
    internal TimeProvider Clock { get; }

    /// <summary>The effective diagnostic profile (<see cref="WorkIqOptions.ProfileOverride"/> over the config's).</summary>
    internal DiagnosticProfile Profile { get; }

    /// <summary>
    /// A stable, non-secret key of the principal this conversation acts for: SHA-256 of <c>tenantId|appClientId|scope</c>.
    /// Task handles carry it, so a handle from another tenant or app is refused.
    /// </summary>
    internal string PrincipalKey => _handles.PrincipalKey;

    /// <summary>The discovered card of this conversation, or null before the first turn.</summary>
    internal RawAgentCard? Card => _discovery.Card;

    /// <summary>The task the service confirmed as <c>TASK_STATE_CANCELED</c> through this conversation, when any.</summary>
    internal string? ConfirmedCanceledTaskId => _state.ConfirmedCanceledTaskId;

    /// <inheritdoc/>
    public string? ContextId => _state.ContextId;

    /// <inheritdoc/>
    public WorkIqTaskHandle? ActiveTask => _state.ActiveTask;

    /// <inheritdoc/>
    public bool IsTurnActive => Volatile.Read(ref _disposed) == 0 && _turnGate.CurrentCount == 0;

    /// <summary>
    /// The issue code of a turn that did not complete: <c>turn.</c> and the outcome in snake case (<c>turn.input_required</c>,
    /// <c>turn.auth_required</c>, <c>turn.failed</c>, <c>turn.indeterminate</c>, and so on). <see cref="WorkIqIssueClassifier"/>
    /// gives a turn result the same code, whatever failure it carries (<see cref="WorkIqIssueClassifier.ForTurnOutcome"/>,
    /// and <see cref="WorkIqIssueClassifier.FromException"/> for a turn's exception). Only a token failure keeps its
    /// <c>auth.*</c> code.
    /// </summary>
    /// <param name="outcome">The outcome.</param>
    internal static string OutcomeIssueCode(WorkIqTurnOutcome outcome)
    {
        var name = outcome.ToString();
        var code = new StringBuilder("turn.", 5 + name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                code.Append('_');
            }

            code.Append(char.ToLowerInvariant(name[i]));
        }

        return code.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Turns
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<WorkIqTurnResult> RunTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        AcquireTurnGate();
        try
        {
            var ctx = BeginTurn(request, WorkIqOperation.Send);
            if (await _discovery.TryDiscoverForTurnAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                using var tokens = CreateTurnTokens(ctx, cancellationToken);

                // A device-code sign-in that starts inside the turn (a token that can no longer be refreshed silently) is
                // bounded by its code's expiry, not by the turn deadline meant for the network. The deadline restarts after it.
                using var signInWait = EnterSignInWait(ctx, tokens);
                await _sender.SendBlockingAsync(ctx, tokens).ConfigureAwait(false);
                if (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working } && ctx.Failure is null && !ctx.Stopped)
                {
                    await _recovery.ReconcileAsync(ctx, tokens).ConfigureAwait(false);
                }
            }

            var result = _finalizer.BuildResult(ctx);
            ThrowIfStoppedLocally(ctx, result, cancellationToken);
            return result;
        }
        finally
        {
            ReleaseTurnGate();
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(StreamingTurn.Send(request, _sender, Client), cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(StreamingTurn.Subscribe(task, _handles, Client), cancellationToken);

    /// <summary>
    /// The one skeleton of a streamed turn and a subscription. Nothing runs before the enumeration starts, so the
    /// argument check, the disposal check and the turn gate apply on the first <c>MoveNextAsync</c>.
    /// </summary>
    /// <param name="turn">What differs between a streamed turn and a subscription.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    private async IAsyncEnumerable<WorkIqStreamEvent> RunStreamingAsync(StreamingTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        turn.ThrowIfArgumentMissing();
        ThrowIfDisposed();
        AcquireTurnGate();
        try
        {
            var ctx = BeginTurn(turn.CreateRequest(), turn.Operation);
            turn.Bind(ctx);
            if (await _discovery.TryDiscoverForTurnAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                turn.Validate(ctx);
                using var tokens = CreateTurnTokens(ctx, cancellationToken);

                // A device-code sign-in that starts inside the turn is bounded by its code's expiry, as in a blocking turn.
                using var signInWait = EnterSignInWait(ctx, tokens);
                var call = ctx.Call();
                var frames = OpenStream(ctx, turn, tokens, call);
                if (frames is not null)
                {
                    await foreach (var evt in _projector.ConsumeFramesAsync(ctx, frames, turn.Source, tokens).ConfigureAwait(false))
                    {
                        yield return evt;
                    }

                    // The scope above does not survive a yield: the iterator resumes on the consumer's flow. Enter it again
                    // for the calls after the stream (the GetTask reconciliation), which may start a sign-in too.
                    using var afterStream = EnterSignInWait(ctx, tokens);
                    ctx.AttachStreamCapture(call);
                    turn.CheckStreamEnd(ctx);
                    await _recovery.ReconcileAfterStreamAsync(ctx, tokens).ConfigureAwait(false);
                }
            }

            var result = _finalizer.BuildResult(ctx);
            ThrowIfStoppedLocally(ctx, result, cancellationToken);
            yield return new WorkIqStreamEvent.EvidenceObserved(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result.Evidence);
            yield return new WorkIqStreamEvent.TurnFinished(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result);
        }
        finally
        {
            ReleaseTurnGate();
        }
    }

    /// <summary>Dispatches the stream and opens it. A call that throws is recorded as a send failure, and the result is null.</summary>
    private IAsyncEnumerable<WorkIqStreamFrame>? OpenStream(TurnContext ctx, StreamingTurn turn, TurnTokens tokens, WorkIqCallContext call)
    {
        var open = turn.Dispatch(ctx, tokens, call);
        try
        {
            return open();
        }
        catch (Exception ex)
        {
            _failures.RecordSendFailure(ctx, ex, tokens, turn.Operation);
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Explicit task reads
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<WorkIqTaskSnapshot> GetTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();

        // An explicit read is no turn: the client registers the issue of its failure itself, and captures go under no-turn.
        var card = await _discovery.DiscoverAsync(call: null, cancellationToken).ConfigureAwait(false);
        _handles.Validate(task, card);

        var reply = await Client.GetTaskAsync(task.TaskId, Options.HistoryLength, cancellationToken).ConfigureAwait(false);
        var snapshot = _handles.SnapshotOf(Card!, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture);
        Diagnostics.Trace(
            WorkIqTraceStage.GetTask,
            "task.state",
            $"GetTask: task is {snapshot.RawState ?? "(state absent)"} with {snapshot.Artifacts.Length} artifact(s)",
            taskId: snapshot.Handle.TaskId,
            contextId: snapshot.Handle.ContextId,
            requestId: reply.RequestId,
            httpStatus: reply.HttpStatus,
            a2aState: snapshot.RawState);
        _state.RecordExplicitRead(snapshot, reply.Capture);
        return snapshot;
    }

    /// <inheritdoc/>
    public async Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        var card = await _discovery.DiscoverAsync(call: null, cancellationToken).ConfigureAwait(false);
        _handles.Validate(task, card);

        Diagnostics.Trace(
            WorkIqTraceStage.Cancel,
            "cancel.requested",
            "CancelTask requested explicitly (best effort: canceled only if the returned state says so)",
            taskId: task.TaskId,
            contextId: task.ContextId);
        var reply = await Client.CancelTaskAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        var snapshot = _handles.SnapshotOf(Card!, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture);
        var confirmed = snapshot.State == WorkIqTaskState.Canceled;
        Diagnostics.Trace(
            WorkIqTraceStage.Cancel,
            confirmed ? "cancel.confirmed" : "cancel.not_confirmed",
            confirmed ? "the service confirmed TASK_STATE_CANCELED" : $"the service replied with {snapshot.RawState ?? "(state absent)"}: the task was not canceled (completed race or not cancelable)",
            confirmed ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning,
            taskId: snapshot.Handle.TaskId,
            contextId: snapshot.Handle.ContextId,
            requestId: reply.RequestId,
            httpStatus: reply.HttpStatus,
            a2aState: snapshot.RawState);
        _state.RecordExplicitRead(snapshot, reply.Capture);
        return snapshot;
    }

    /// <inheritdoc/>
    public Task<WorkIqTurnResult> ContinueAsync(WorkIqPendingInteraction interaction, WorkIqInteractionResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(response);
        throw new NotSupportedException(ContinueNotSupportedMessage);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reset, abandon, dispose
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // The turn gate is held for the reset, so no turn can start on the context being dropped.
        if (!_turnGate.Wait(0))
        {
            throw new InvalidOperationException("ResetSessionAsync is rejected while a turn or subscription is active; call AbandonAndResetAsync to stop it first.");
        }

        string? dropped;
        try
        {
            dropped = _state.Reset();
        }
        finally
        {
            ReleaseTurnGate();
        }

        Diagnostics.Trace(
            WorkIqTraceStage.Outcome,
            "turn.context_reset",
            dropped is null
                ? "no local context to drop; the next turn starts a fresh server context (no remote call)"
                : "local context dropped; the next turn starts a fresh server context (no remote call; nothing was canceled or deleted remotely)",
            contextId: dropped);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<WorkIqAbandonmentReceipt> AbandonAndResetAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ThrowIfDisposed();

        var (activeContext, activeTask, _, _) = _state.Peek();
        var wasActive = IsTurnActive;
        var previous = Interlocked.Exchange(ref _abandonCts, new CancellationTokenSource());
        previous.Cancel();
        Diagnostics.Trace(
            WorkIqTraceStage.Outcome,
            "turn.abandon_requested",
            wasActive ? "abandoning the active operation: local waiting stopped; no CancelTask is sent" : "nothing is active; clearing the local context (no CancelTask is sent)",
            WorkIqTraceLevel.Warning,
            taskId: activeTask?.TaskId,
            contextId: activeContext);

        // The gate is held while the state is reset, so no new turn can start on the context being dropped. It is free
        // when nothing was active; when a turn was, it is taken once that turn ends, within the recovery budget.
        var gateHeld = wasActive ? await WaitForActiveTurnAsync(cancellationToken).ConfigureAwait(false) : _turnGate.Wait(0);
        var quiesced = !wasActive || gateHeld;
        (string? ContextId, WorkIqTaskHandle? Task, WorkIqTurnOutcome? Outcome, RawCaptureRef? Capture) prior;
        try
        {
            previous.Dispose();

            // Read after the wait: a turn that stopped has recorded its own task and outcome, and one that did not stop
            // is named by its running task with an indeterminate outcome. Never the previous turn's.
            prior = _state.ResetAndDescribe();
        }
        finally
        {
            if (gateHeld)
            {
                ReleaseTurnGate();
            }
        }

        var remoteConfirmed = prior.Task is not null && string.Equals(ConfirmedCanceledTaskId, prior.Task.TaskId, StringComparison.Ordinal);
        TraceAbandoned(quiesced, prior.Outcome, remoteConfirmed, prior.Task, prior.ContextId);
        return new WorkIqAbandonmentReceipt(reason, Clock.GetUtcNow(), quiesced, prior.ContextId, prior.Task, prior.Outcome, prior.Capture, remoteConfirmed);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _abandonCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _abandonCts.Dispose();
        _turnGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Waits until the active turn ends and takes the turn gate, within <see cref="WorkIqOptions.RecoveryBudget"/> (a
    /// zero budget does not wait). The caller releases the gate. A stop by the caller's token propagates.
    /// </summary>
    /// <returns>True when the turn ended and the gate is held; false when the turn did not end within the budget.</returns>
    private async Task<bool> WaitForActiveTurnAsync(CancellationToken cancellationToken)
    {
        using var budget = Options.RecoveryBudget > TimeSpan.Zero ? new CancellationTokenSource(Options.RecoveryBudget, Clock) : new CancellationTokenSource();
        if (Options.RecoveryBudget <= TimeSpan.Zero)
        {
            budget.Cancel();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
        try
        {
            await _turnGate.WaitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void TraceAbandoned(bool quiesced, WorkIqTurnOutcome? lastOutcome, bool remoteConfirmed, WorkIqTaskHandle? priorTask, string? priorContext)
    {
        Diagnostics.Trace(
            WorkIqTraceStage.Outcome,
            quiesced ? "turn.abandoned" : "turn.abandon_not_quiesced",
            quiesced
                ? $"local context cleared; last outcome {lastOutcome?.ToString() ?? "(none)"}; remote cancellation confirmed: {remoteConfirmed}"
                : $"the active operation did not stop within the recovery budget ({Options.RecoveryBudget}); the context was cleared but no new turn may start until it ends",
            quiesced ? WorkIqTraceLevel.Warning : WorkIqTraceLevel.Error,
            taskId: priorTask?.TaskId,
            contextId: priorContext);
        if (!quiesced)
        {
            Diagnostics.Issue(
                WorkIqIssue.Create(
                    WorkIqTraceStage.Outcome,
                    WorkIqIssueCategory.Budget,
                    "turn.abandon_not_quiesced",
                    "An abandoned operation did not stop within the recovery budget.",
                    "The transport is blocked in a read that ignores cancellation, or the recovery budget is too short; the remote task may still be running.") with
                {
                    TaskId = priorTask?.TaskId,
                    ContextId = priorContext,
                });
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Turn lifecycle helpers
    // ---------------------------------------------------------------------------------------------------------

    private TurnContext BeginTurn(WorkIqTurnRequest request, WorkIqOperation operation)
    {
        var (turnIndex, epoch, reuse) = _state.StartTurn();
        var identity = new TurnIdentity(
            Diagnostics.RunId,
            request.CaseId,
            request.TurnId,
            turnIndex,
            0,
            WorkIqA2AClient.NewRpcId(),
            Guid.NewGuid().ToString("D"),
            ProviderMessageId: null,
            ProviderTaskId: null,
            ProviderContextId: null);
        var ctx = new TurnContext(request, identity, operation, Clock.GetUtcNow(), epoch, _abandonCts.Token, Diagnostics)
        {
            CaptureScope = TurnContext.CaptureScopeFor(Diagnostics.RunId, request.TurnId),
        };

        var continues = Options.ConversationPolicy == ConversationPolicy.ContinueContext && reuse is not null;
        ctx.Trace(
            WorkIqTraceStage.Outcome,
            "turn.started",
            $"turn {turnIndex} ({operation}) started under {Options.ConversationPolicy}: {(continues ? "continuing the server context" : "fresh server context")}; " +
            $"profile {Profile}; {request.Input.Length} input characters",
            contextId: continues ? reuse : null);
        return ctx;
    }

    // The abandonment token the turn captured when it started: an abandonment during discovery swaps in a new source,
    // and the turn must keep the one that fired.
    private TurnTokens CreateTurnTokens(TurnContext ctx, CancellationToken callerToken) =>
        TurnTokens.Create(callerToken, ctx.AbandonToken, Options.TurnDeadline, Clock);

    private IDisposable EnterSignInWait(TurnContext ctx, TurnTokens tokens) =>
        TurnSignInWait.Enter(ctx, tokens, Clock, Options.TurnDeadline);

    private static void ThrowIfStoppedLocally(TurnContext ctx, WorkIqTurnResult result, CancellationToken callerToken)
    {
        if (ctx.CanceledByCaller)
        {
            throw new WorkIqTurnCanceledException(result, callerToken);
        }

        if (ctx.Abandoned)
        {
            throw new WorkIqTurnCanceledException(result, ctx.AbandonToken);
        }
    }

    private void AcquireTurnGate()
    {
        if (!_turnGate.Wait(0))
        {
            throw new WorkIqTurnRefusedException("one active turn per conversation: a turn or subscription already owns this conversation; await it or call AbandonAndResetAsync first.");
        }
    }

    private void ReleaseTurnGate()
    {
        try
        {
            _turnGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while the turn ran; nothing to release.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WorkIqConversation));
        }
    }
}
