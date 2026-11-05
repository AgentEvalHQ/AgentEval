// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365;

/// <summary>
/// The production <see cref="IWorkIqConversation"/>: owns the server <c>contextId</c> and the current task handle,
/// serialises turns behind a single-active-turn guard, runs the blocking turn path (send → bounded
/// <c>GetTask</c> reconciliation), the streaming path, recovery after a drop, reset and abandonment, and
/// normalises evidence exactly once per turn. Card discovery runs once per conversation and the card digest is
/// recorded. Constructed by <see cref="Microsoft365AgentFactory.BuildLive"/> (internal constructor); tests use the
/// <see cref="IWorkIqConversation"/> seam instead.
/// </summary>
/// <remarks>
/// <para>
/// Every turn yields a typed <see cref="WorkIqTurnResult"/>: a transport failure whose remote state is known
/// (an HTTP error reply, a JSON-RPC error, a refused card) ends as <see cref="WorkIqTurnOutcome.Failed"/>; one
/// whose state is unknown (connection loss, timeout, local stop before a task id) ends as
/// <see cref="WorkIqTurnOutcome.Indeterminate"/>. The input is never resent. A stop driven by the caller's
/// token (or by <see cref="AbandonAndResetAsync"/>) throws <see cref="WorkIqTurnCanceledException"/> carrying that
/// result. Only a discovery failure on an explicit task read (<see cref="GetTaskAsync"/>, <see cref="CancelTaskAsync"/>)
/// surfaces as <see cref="WorkIqProtocolException"/>, because no turn result exists there.
/// </para>
/// <para>
/// Zero-valued budgets: a zero <see cref="WorkIqOptions.TurnDeadline"/> means no deadline; a zero
/// <see cref="WorkIqOptions.RecoveryBudget"/> means no <c>GetTask</c> polling after a non-terminal reply (the turn
/// is then <see cref="WorkIqTurnOutcome.Indeterminate"/> with a timeout failure).
/// </para>
/// </remarks>
public sealed class WorkIqConversation : IWorkIqConversation
{
    /// <summary>The versioned interaction profile that recognises Work IQ pauses (confirmation cards, auth-required payloads); the evidence decoder's id.</summary>
    public const string InteractionProfileId = InteractionDecoder.ProfileId;

    /// <summary>The key of the pause payload inside a status-message data part for <c>TASK_STATE_INPUT_REQUIRED</c>.</summary>
    public const string InputRequiredKey = InteractionDecoder.InputRequiredKey;

    /// <summary>The key of the pause payload inside a status-message data part for <c>TASK_STATE_AUTH_REQUIRED</c>.</summary>
    public const string AuthRequiredKey = InteractionDecoder.AuthRequiredKey;

    /// <summary>The bound of the opt-in <c>CancelTask</c> sent after a local timeout (<see cref="WorkIqOptions.CancelRemoteTaskOnTimeout"/>).</summary>
    internal static readonly TimeSpan RemoteCancelTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource _abandonCts = new();
    private RawAgentCard? _card;
    private string? _contextId;
    private WorkIqTaskHandle? _activeTask;
    private WorkIqTaskHandle? _lastTask;
    private WorkIqTurnOutcome? _lastOutcome;
    private RawCaptureRef? _lastCapture;
    private string? _confirmedCanceledTaskId;
    private int _turnIndex;
    private int _epoch;
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
        PrincipalKey = ComputePrincipalKey(config.TenantId, config.AppClientId, config.Scope);
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
    /// A stable, non-secret key of the principal this conversation acts for: SHA-256 of <c>tenantId|appClientId|scope</c>
    /// (the signed-in account is not known at this layer and no token is ever used). Task handles carry it so a
    /// handle from another tenant/app is refused.
    /// </summary>
    internal string PrincipalKey { get; }

    /// <summary>The discovered card of this conversation, or null before the first turn.</summary>
    internal RawAgentCard? Card => Volatile.Read(ref _card);

    /// <summary>The task the service confirmed as <c>TASK_STATE_CANCELED</c> through this conversation, when any.</summary>
    internal string? ConfirmedCanceledTaskId => Volatile.Read(ref _confirmedCanceledTaskId);

    /// <inheritdoc/>
    public string? ContextId
    {
        get
        {
            lock (_stateGate)
            {
                return _contextId;
            }
        }
    }

    /// <inheritdoc/>
    public WorkIqTaskHandle? ActiveTask
    {
        get
        {
            lock (_stateGate)
            {
                return _activeTask;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsTurnActive => Volatile.Read(ref _disposed) == 0 && _turnGate.CurrentCount == 0;

    // ---------------------------------------------------------------------------------------------------------
    // Blocking turn
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
            if (await TryDiscoverAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                using var tokens = CreateTurnTokens(cancellationToken);
                await SendBlockingAsync(ctx, tokens, cancellationToken).ConfigureAwait(false);
                if (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working } && ctx.Failure is null && !ctx.Stopped)
                {
                    await ReconcileAsync(ctx, tokens, cancellationToken).ConfigureAwait(false);
                }
            }

            var result = FinalizeTurn(ctx);
            ThrowIfStoppedLocally(ctx, result, cancellationToken);
            return result;
        }
        finally
        {
            ReleaseTurnGate();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Streaming turn + subscription (plumbing over lane C's streaming members)
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public async IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        AcquireTurnGate();
        try
        {
            var ctx = BeginTurn(request, WorkIqOperation.Stream);
            if (await TryDiscoverAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                using var tokens = CreateTurnTokens(cancellationToken);
                var sendRequest = BuildSendRequest(ctx);
                ctx.SentContextId = sendRequest.Message.ContextId;
                TraceDispatch(ctx, sendRequest, "SendStreamingMessage");

                IAsyncEnumerable<WorkIqStreamFrame>? frames = null;
                try
                {
                    SetPendingRpcId(ctx);
                    frames = Client.StreamAsync(sendRequest, tokens.Token);
                }
                catch (Exception ex)
                {
                    RecordSendFailure(ctx, ex, tokens, cancellationToken, WorkIqOperation.Stream);
                }

                if (frames is not null)
                {
                    await foreach (var evt in ConsumeFramesAsync(ctx, frames, WorkIqSnapshotSource.Stream, tokens).ConfigureAwait(false))
                    {
                        yield return evt;
                    }

                    AttachStreamCapture(ctx);
                    await ReconcileAfterStreamAsync(ctx, tokens, cancellationToken, WorkIqOperation.Stream).ConfigureAwait(false);
                }
            }

            var result = FinalizeTurn(ctx);
            ThrowIfStoppedLocally(ctx, result, cancellationToken);
            yield return new WorkIqStreamEvent.EvidenceObserved(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result.Evidence);
            yield return new WorkIqStreamEvent.TurnFinished(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result);
        }
        finally
        {
            ReleaseTurnGate();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        AcquireTurnGate();
        try
        {
            var request = new WorkIqTurnRequest("", "subscribe:" + task.TaskId, "subscribe-" + Guid.NewGuid().ToString("N"));
            var ctx = BeginTurn(request, WorkIqOperation.Subscribe);
            ctx.Handle = task;
            ctx.ContextId = task.ContextId;
            if (await TryDiscoverAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                ValidateHandle(task, ctx.Card!);
                using var tokens = CreateTurnTokens(cancellationToken);
                Diagnostics.Trace(WorkIqTraceStage.Subscribe, "subscribe.dispatch", "SubscribeToTask dispatched (never resubmits input; the first frame must be a task snapshot)", caseId: request.CaseId, turnId: request.TurnId, taskId: task.TaskId, contextId: task.ContextId);

                IAsyncEnumerable<WorkIqStreamFrame>? frames = null;
                try
                {
                    frames = Client.SubscribeAsync(task.TaskId, tokens.Token);
                }
                catch (Exception ex)
                {
                    RecordSendFailure(ctx, ex, tokens, cancellationToken, WorkIqOperation.Subscribe);
                }

                if (frames is not null)
                {
                    await foreach (var evt in ConsumeFramesAsync(ctx, frames, WorkIqSnapshotSource.Subscribe, tokens).ConfigureAwait(false))
                    {
                        yield return evt;
                    }

                    AttachStreamCapture(ctx);
                    if (ctx.Snapshot is null && ctx.DirectMessage is null && ctx.Failure is null)
                    {
                        ctx.Failure = new WorkIqFailure(
                            WorkIqFailureCategory.Malformed,
                            "Subscribe: the subscription ended without a first task snapshot (protocol/service deviation); local state was not replaced",
                            OutcomeCertainty.Indeterminate,
                            WorkIqOperation.Subscribe,
                            TaskId: task.TaskId,
                            ContextId: task.ContextId,
                            RpcRequestId: ctx.Identity.RpcRequestId);
                        ctx.Transport = TransportOutcome.Failed;
                    }

                    await ReconcileAfterStreamAsync(ctx, tokens, cancellationToken, WorkIqOperation.Subscribe).ConfigureAwait(false);
                }
            }

            var result = FinalizeTurn(ctx);
            ThrowIfStoppedLocally(ctx, result, cancellationToken);
            yield return new WorkIqStreamEvent.EvidenceObserved(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result.Evidence);
            yield return new WorkIqStreamEvent.TurnFinished(ctx.Request.TurnId, ctx.NextSequence(), Clock.GetUtcNow(), result);
        }
        finally
        {
            ReleaseTurnGate();
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
        var card = await EnsureCardAsync(cancellationToken).ConfigureAwait(false);
        ValidateHandle(task, card);

        var reply = await Client.GetTaskAsync(task.TaskId, Options.HistoryLength, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotOf(reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture);
        Diagnostics.Trace(WorkIqTraceStage.GetTask, "task.state", $"GetTask: task is {snapshot.RawState ?? "(state absent)"} with {snapshot.Artifacts.Length} artifact(s)", taskId: snapshot.Handle.TaskId, contextId: snapshot.Handle.ContextId, requestId: reply.RequestId, httpStatus: reply.HttpStatus, a2aState: snapshot.RawState);
        RecordExplicitRead(snapshot, reply.Capture);
        return snapshot;
    }

    /// <inheritdoc/>
    public async Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        var card = await EnsureCardAsync(cancellationToken).ConfigureAwait(false);
        ValidateHandle(task, card);

        Diagnostics.Trace(WorkIqTraceStage.Cancel, "cancel.requested", "CancelTask requested explicitly (best effort: canceled only if the returned state says so)", taskId: task.TaskId, contextId: task.ContextId);
        var reply = await Client.CancelTaskAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotOf(reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture);
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
        RecordExplicitRead(snapshot, reply.Capture);
        return snapshot;
    }

    /// <inheritdoc/>
    public Task<WorkIqTurnResult> ContinueAsync(WorkIqPendingInteraction interaction, WorkIqInteractionResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(response);
        throw new NotSupportedException("TODO(M53): interaction continuation lands in wave 5; the pending interaction is preserved on the turn result");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reset, abandon, dispose
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsTurnActive)
        {
            throw new InvalidOperationException("ResetSessionAsync is rejected while a turn or subscription is active; call AbandonAndResetAsync to stop it first.");
        }

        string? dropped;
        lock (_stateGate)
        {
            _epoch++;
            dropped = _contextId;
            _contextId = null;
            _activeTask = null;
            _lastTask = null;
            _lastOutcome = null;
            _lastCapture = null;
        }

        Diagnostics.Trace(WorkIqTraceStage.Outcome, "turn.context_reset", dropped is null ? "no local context to drop; the next turn starts a fresh server context (no remote call)" : "local context dropped; the next turn starts a fresh server context (no remote call; nothing was canceled or deleted remotely)", contextId: dropped);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<WorkIqAbandonmentReceipt> AbandonAndResetAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ThrowIfDisposed();

        string? priorContext;
        WorkIqTaskHandle? priorTask;
        WorkIqTurnOutcome? lastOutcome;
        RawCaptureRef? lastCapture;
        lock (_stateGate)
        {
            priorContext = _contextId;
            priorTask = _lastTask ?? _activeTask;
            lastOutcome = _lastOutcome;
            lastCapture = _lastCapture;
        }

        var wasActive = IsTurnActive;
        var previous = Interlocked.Exchange(ref _abandonCts, new CancellationTokenSource());
        previous.Cancel();
        Diagnostics.Trace(WorkIqTraceStage.Outcome, "turn.abandon_requested", wasActive ? "abandoning the active operation: local waiting stopped; no CancelTask is sent" : "nothing is active; clearing the local context (no CancelTask is sent)", WorkIqTraceLevel.Warning, taskId: priorTask?.TaskId, contextId: priorContext);

        var quiesced = true;
        if (wasActive)
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
                _turnGate.Release();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                quiesced = false;
            }
        }

        previous.Dispose();
        lock (_stateGate)
        {
            _epoch++;
            _contextId = null;
            _activeTask = null;
            _lastTask = null;
            _lastOutcome = null;
            _lastCapture = null;
        }

        var remoteConfirmed = priorTask is not null && string.Equals(ConfirmedCanceledTaskId, priorTask.TaskId, StringComparison.Ordinal);
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
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Outcome, WorkIqIssueCategory.Service, "turn.abandon_not_quiesced", "An abandoned operation did not stop within the recovery budget.", "The transport is blocked in a read that ignores cancellation, or the recovery budget is too short; the remote task may still be running.") with { TaskId = priorTask?.TaskId, ContextId = priorContext });
        }

        return new WorkIqAbandonmentReceipt(reason, Clock.GetUtcNow(), quiesced, priorContext, priorTask, lastOutcome, lastCapture, remoteConfirmed);
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

    // ---------------------------------------------------------------------------------------------------------
    // Turn lifecycle helpers
    // ---------------------------------------------------------------------------------------------------------

    private TurnContext BeginTurn(WorkIqTurnRequest request, WorkIqOperation operation)
    {
        int turnIndex;
        int epoch;
        string? reuse;
        lock (_stateGate)
        {
            turnIndex = _turnIndex++;
            epoch = _epoch;
            reuse = _contextId;
        }

        var identity = new TurnIdentity(
            Diagnostics.RunId,
            request.CaseId,
            request.TurnId,
            turnIndex,
            0,
            NewRpcId(),
            Guid.NewGuid().ToString("D"),
            ProviderMessageId: null,
            ProviderTaskId: null,
            ProviderContextId: null);
        var ctx = new TurnContext(request, identity, operation, Clock.GetUtcNow(), epoch, _abandonCts.Token);

        if (Client is WorkIqA2AClient concrete)
        {
            concrete.CaptureScope = SanitizePathSegment(Diagnostics.RunId) + "/" + SanitizePathSegment(request.TurnId);
        }

        var continues = Options.ConversationPolicy == ConversationPolicy.ContinueContext && reuse is not null;
        Diagnostics.Trace(
            WorkIqTraceStage.Outcome,
            "turn.started",
            $"turn {turnIndex} ({operation}) started under {Options.ConversationPolicy}: {(continues ? "continuing the server context" : "fresh server context")}; " +
            $"profile {Profile}; {request.Input.Length} input characters",
            caseId: request.CaseId,
            turnId: request.TurnId,
            contextId: continues ? reuse : null);
        return ctx;
    }

    private async Task<bool> TryDiscoverAsync(TurnContext ctx, CancellationToken cancellationToken)
    {
        if (Card is { } cached)
        {
            ctx.Card = cached;
            return true;
        }

        try
        {
            var card = await Client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _card, card);
            ctx.Card = card;
            Diagnostics.Trace(
                WorkIqTraceStage.Discovery,
                "discovery.card_recorded",
                $"card sha256 {card.Sha256} from {card.CardUri.Host}{card.CardUri.AbsolutePath}; endpoint {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath} (A2A {card.SelectedProtocolVersion}) recorded for this conversation",
                caseId: ctx.Request.CaseId,
                turnId: ctx.Request.TurnId);
            return true;
        }
        catch (Exception ex)
        {
            var failure = WorkIqA2AClient.Classify(ex, WorkIqOperation.Discover, requestId: null, callerToken: cancellationToken, scope: Config.Scope)
                with { RpcRequestId = ctx.Identity.RpcRequestId };
            ctx.CanceledByCaller = cancellationToken.IsCancellationRequested;
            ctx.Failure = failure;
            ctx.Transport = TransportOutcome.Failed;
            if (failure.Capture is not null)
            {
                ctx.AddCapture(failure.Capture);
            }

            Diagnostics.Trace(WorkIqTraceStage.Discovery, "discovery.turn_blocked", $"the turn cannot start: {failure.Category} ({failure.Certainty}): {failure.SafeMessage}", WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, httpStatus: failure.HttpStatus, rpcErrorCode: failure.RpcErrorCode);
            return false;
        }
    }

    private async Task<RawAgentCard> EnsureCardAsync(CancellationToken cancellationToken)
    {
        if (Card is { } cached)
        {
            return cached;
        }

        var card = await Client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _card, card);
        return card;
    }

    private SendMessageRequest BuildSendRequest(TurnContext ctx)
    {
        string? reuse;
        lock (_stateGate)
        {
            reuse = Options.ConversationPolicy == ConversationPolicy.ContinueContext ? _contextId : null;
        }

        var message = new Message
        {
            MessageId = ctx.Identity.ClientMessageId,
            Role = Role.User,
            Parts = [Part.FromText(ctx.Request.Input)],
            ContextId = reuse,
            TaskId = null,
            Metadata = BuildLocationMetadata(Options.EvaluationReferenceInstant ?? ctx.StartedAt),
        };
        var configuration = new SendMessageConfiguration
        {
            ReturnImmediately = false,
            HistoryLength = Options.HistoryLength,
            AcceptedOutputModes = WorkIqA2AClient.AcceptedOutputModesFor(Profile),
        };
        return new SendMessageRequest { Message = message, Configuration = configuration };
    }

    /// <summary>
    /// The <c>Location</c> metadata of a user message when <see cref="Microsoft365Config.TimeZone"/> is set: the IANA
    /// id and the DST-aware offset (minutes) of that zone at <paramref name="instant"/>; null when no zone is set.
    /// </summary>
    /// <param name="instant">The evaluation reference instant (<see cref="WorkIqOptions.EvaluationReferenceInstant"/> or the send time).</param>
    internal Dictionary<string, JsonElement>? BuildLocationMetadata(DateTimeOffset instant)
    {
        if (string.IsNullOrWhiteSpace(Config.TimeZone))
        {
            return null;
        }

        var zoneId = Config.TimeZone.Trim();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var offsetMinutes = (int)Math.Round(zone.GetUtcOffset(instant).TotalMinutes);
        var location = JsonSerializer.SerializeToElement(new LocationMetadata(zoneId, offsetMinutes), LocationJsonOptions);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["Location"] = location };
    }

    private void TraceDispatch(TurnContext ctx, SendMessageRequest request, string method)
    {
        Diagnostics.Trace(new WorkIqTraceEvent(default, ctx.Operation == WorkIqOperation.Stream ? WorkIqTraceStage.Stream : WorkIqTraceStage.Send, WorkIqTraceLevel.Info, "send.dispatch",
            $"{method} dispatched once (returnImmediately=false, context {(request.Message.ContextId is null ? "omitted" : "reused")}, no task id, " +
            $"historyLength {(request.Configuration?.HistoryLength?.ToString(CultureInfo.InvariantCulture) ?? "omitted")}, " +
            $"modes {string.Join("+", request.Configuration?.AcceptedOutputModes ?? [])}, Location metadata {(request.Message.Metadata is null ? "absent" : "present")})")
        {
            CaseId = ctx.Request.CaseId,
            TurnId = ctx.Request.TurnId,
            RpcId = ctx.Identity.RpcRequestId,
            MessageId = ctx.Identity.ClientMessageId,
            ContextId = request.Message.ContextId,
        });
    }

    private void SetPendingRpcId(TurnContext ctx)
    {
        if (Client is WorkIqA2AClient concrete)
        {
            concrete.NextRpcId = ctx.Identity.RpcRequestId;
        }
    }

    private void ClearPendingRpcId(TurnContext ctx)
    {
        if (Client is WorkIqA2AClient concrete && string.Equals(concrete.NextRpcId, ctx.Identity.RpcRequestId, StringComparison.Ordinal))
        {
            concrete.NextRpcId = null;
        }
    }

    private async Task SendBlockingAsync(TurnContext ctx, TurnTokens tokens, CancellationToken callerToken)
    {
        var request = BuildSendRequest(ctx);
        ctx.SentContextId = request.Message.ContextId;
        TraceDispatch(ctx, request, "SendMessage");

        WorkIqRpcResult<SendMessageResponse> reply;
        try
        {
            SetPendingRpcId(ctx);
            reply = await Client.SendAsync(request, tokens.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ClearPendingRpcId(ctx);
            RecordSendFailure(ctx, ex, tokens, callerToken, WorkIqOperation.Send);
            return;
        }

        ClearPendingRpcId(ctx);
        ctx.FirstEventAt ??= reply.ReceivedAt;
        ctx.AddCapture(reply.Capture);

        if (reply.Value.Task is { } task)
        {
            ApplyTask(ctx, task, WorkIqSnapshotSource.Send, reply.Capture, reply.ReceivedAt);
        }
        else if (reply.Value.Message is { } message)
        {
            ApplyDirectMessage(ctx, WorkIqSdkMapping.ToMessage(message), reply.ReceivedAt);
        }
        else
        {
            ctx.Failure = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                "Send: the reply carries neither a task nor a message",
                OutcomeCertainty.Known,
                WorkIqOperation.Send,
                HttpStatus: reply.HttpStatus,
                RequestId: reply.RequestId,
                RpcRequestId: ctx.Identity.RpcRequestId,
                Capture: reply.Capture);
            ctx.Transport = TransportOutcome.Failed;
        }
    }

    private void RecordSendFailure(TurnContext ctx, Exception ex, TurnTokens tokens, CancellationToken callerToken, WorkIqOperation operation)
    {
        var failure = WorkIqA2AClient.Classify(ex, operation, requestId: null, taskId: null, contextId: ctx.SentContextId, callerToken: tokens.Token, scope: Config.Scope)
            with { RpcRequestId = ctx.Identity.RpcRequestId };
        if (failure.Category == WorkIqFailureCategory.Canceled)
        {
            failure = RefineLocalStop(ctx, failure, tokens, callerToken, operation, "before a reply");
        }

        if (failure.Capture is not null)
        {
            ctx.AddCapture(failure.Capture);
        }

        ctx.Failure = failure;
        ctx.Transport = TransportOutcome.Failed;
        Diagnostics.Trace(
            operation == WorkIqOperation.Send ? WorkIqTraceStage.Send : operation == WorkIqOperation.Subscribe ? WorkIqTraceStage.Subscribe : WorkIqTraceStage.Stream,
            "send.failed",
            $"{operation} failed before a task id was known: {failure.Category} ({failure.Certainty}): {failure.SafeMessage}; the input was NOT resent",
            WorkIqTraceLevel.Error,
            caseId: ctx.Request.CaseId,
            turnId: ctx.Request.TurnId,
            contextId: ctx.SentContextId,
            requestId: failure.RequestId,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode);
    }

    /// <summary>Refines a <see cref="WorkIqFailureCategory.Canceled"/> classification by which local source fired: the caller's token, an abandonment, or the turn deadline (→ Timeout).</summary>
    private WorkIqFailure RefineLocalStop(TurnContext ctx, WorkIqFailure failure, TurnTokens tokens, CancellationToken callerToken, WorkIqOperation operation, string phase)
    {
        if (callerToken.IsCancellationRequested)
        {
            ctx.CanceledByCaller = true;
            return failure with { SafeMessage = $"{operation}: the caller's token stopped the local wait {phase}; the remote state was not reconciled and the input was not resent" };
        }

        if (tokens.Abandon.IsCancellationRequested)
        {
            ctx.Abandoned = true;
            return failure with { SafeMessage = $"{operation}: abandoned locally (AbandonAndResetAsync) {phase}; the remote state was not reconciled and the input was not resent" };
        }

        if (tokens.DeadlineFired)
        {
            ctx.DeadlineFired = true;
            return failure with
            {
                Category = WorkIqFailureCategory.Timeout,
                SafeMessage = $"{operation}: the turn deadline ({Options.TurnDeadline}) elapsed {phase}; the request may have reached the service and the input was not resent",
            };
        }

        return failure;
    }

    private void ApplyTask(TurnContext ctx, AgentTask task, WorkIqSnapshotSource source, RawCaptureRef? capture, DateTimeOffset at)
    {
        var reducer = ctx.Reducer ??= CreateReducer(ctx);
        reducer.Load(task, source, null, capture);
        var snapshot = reducer.Snapshot() ?? throw new InvalidOperationException("The task reducer produced no snapshot after loading a task.");
        var handle = BuildHandle(ctx.Card!, task.Id, task.ContextId);
        AdoptSnapshot(ctx, snapshot with { Handle = handle, Capture = capture ?? snapshot.Capture }, at, source);
    }

    private void AdoptSnapshot(TurnContext ctx, WorkIqTaskSnapshot snapshot, DateTimeOffset at, WorkIqSnapshotSource source)
    {
        var previous = ctx.Snapshot;
        ctx.Snapshot = snapshot;
        ctx.Handle = snapshot.Handle;
        if (!string.IsNullOrWhiteSpace(snapshot.Handle.ContextId))
        {
            ctx.ContextId = snapshot.Handle.ContextId;
        }

        if (ctx.FirstAnswerAt is null && HasAnswerText(snapshot))
        {
            ctx.FirstAnswerAt = at;
        }

        if (snapshot.IsTerminal || snapshot.IsSuspended)
        {
            ctx.TerminalAt ??= at;
        }

        if (previous is null || previous.State != snapshot.State || !string.Equals(previous.RawState, snapshot.RawState, StringComparison.Ordinal))
        {
            Diagnostics.Trace(
                StageOf(source),
                "task.state",
                $"task state {snapshot.RawState ?? "(absent)"} via {source} ({snapshot.Artifacts.Length} artifact(s){(previous is null ? "" : $", was {previous.RawState ?? "(absent)"}")})",
                snapshot.State == WorkIqTaskState.Unknown ? WorkIqTraceLevel.Warning : WorkIqTraceLevel.Info,
                caseId: ctx.Request.CaseId,
                turnId: ctx.Request.TurnId,
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }

        for (var i = ctx.SchemaWarningsTraced; i < snapshot.SchemaWarnings.Length; i++)
        {
            Diagnostics.Trace(WorkIqTraceStage.Reduce, "reduce.schema_warning", snapshot.SchemaWarnings[i], WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: snapshot.Handle.TaskId, contextId: snapshot.Handle.ContextId);
        }

        ctx.SchemaWarningsTraced = Math.Max(ctx.SchemaWarningsTraced, snapshot.SchemaWarnings.Length);
    }

    private void ApplyDirectMessage(TurnContext ctx, WorkIqMessage message, DateTimeOffset at)
    {
        ctx.DirectMessage = message;
        if (!string.IsNullOrWhiteSpace(message.ContextId))
        {
            ctx.ContextId = message.ContextId;
        }

        ctx.TerminalAt ??= at;
        if (ctx.FirstAnswerAt is null && message.Parts.Any(p => p.Kind == WorkIqPartKind.Text && !string.IsNullOrEmpty(p.Text)))
        {
            ctx.FirstAnswerAt = at;
        }

        Diagnostics.Trace(
            ctx.Operation == WorkIqOperation.Stream ? WorkIqTraceStage.Stream : WorkIqTraceStage.Send,
            "task.state",
            $"direct message reply (no task) with {message.Parts.Length} part(s)",
            caseId: ctx.Request.CaseId,
            turnId: ctx.Request.TurnId,
            contextId: message.ContextId);
    }

    private async Task ReconcileAsync(TurnContext ctx, TurnTokens tokens, CancellationToken callerToken)
    {
        var handle = ctx.Handle ?? throw new InvalidOperationException("Reconciliation needs a task handle.");
        var taskId = handle.TaskId;
        var recoveryStart = Clock.GetUtcNow();
        var budget = Options.RecoveryBudget;
        ClearPendingRpcId(ctx);
        Diagnostics.Trace(
            WorkIqTraceStage.GetTask,
            "recovery.started",
            $"task is {ctx.Snapshot?.RawState ?? "(absent)"}: polling GetTask every {Options.RecoveryPollInterval} within a {budget} budget (the input is never resent)",
            caseId: ctx.Request.CaseId,
            turnId: ctx.Request.TurnId,
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);

        while (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working } current)
        {
            var remaining = budget - (Clock.GetUtcNow() - recoveryStart);
            if (remaining <= TimeSpan.Zero)
            {
                ctx.Failure = new WorkIqFailure(
                    WorkIqFailureCategory.Timeout,
                    $"GetTask: the recovery budget ({budget}) is exhausted after {ctx.RecoveryRequests} poll(s); last observed state {current.RawState ?? "(absent)"}; the input was not resent",
                    OutcomeCertainty.Indeterminate,
                    WorkIqOperation.GetTask,
                    RemoteTaskState: current.RawState,
                    RpcRequestId: ctx.Identity.RpcRequestId,
                    TaskId: taskId,
                    ContextId: ctx.ContextId,
                    Capture: ctx.LastCapture);
                ctx.Transport = TransportOutcome.Interrupted;
                Diagnostics.Trace(WorkIqTraceStage.Budget, "budget.exceeded", ctx.Failure.SafeMessage, WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: taskId, contextId: ctx.ContextId, a2aState: current.RawState);
                Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Budget, WorkIqIssueCategory.Budget, "budget.recovery_exhausted", ctx.Failure.SafeMessage, "The task stayed submitted/working longer than RecoveryBudget allows; raise the budget or inspect the task with GetTask/CancelTask using the handle on the result.") with
                {
                    CaseId = ctx.Request.CaseId,
                    TurnId = ctx.Request.TurnId,
                    TaskId = taskId,
                    ContextId = ctx.ContextId,
                    A2AState = current.RawState,
                });
                await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
                return;
            }

            var wait = Options.RecoveryPollInterval < remaining ? Options.RecoveryPollInterval : remaining;
            try
            {
                if (wait > TimeSpan.Zero)
                {
                    // Arm the poll timer on the injected clock before announcing the sleep: whoever drives that clock
                    // (a test, a replay) can advance it on "recovery.waiting" knowing the timer is already registered,
                    // instead of stepping blindly while a GetTask round-trip is still in flight.
                    var poll = Task.Delay(wait, Clock, tokens.Token);
                    Diagnostics.Trace(
                        WorkIqTraceStage.GetTask,
                        "recovery.waiting",
                        $"sleeping {wait} before GetTask poll #{ctx.RecoveryRequests + 1}; {remaining} of the {budget} recovery budget remain",
                        caseId: ctx.Request.CaseId,
                        turnId: ctx.Request.TurnId,
                        taskId: taskId,
                        contextId: ctx.ContextId,
                        a2aState: current.RawState);
                    await poll.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                RecordLocalStop(ctx, tokens, callerToken, WorkIqOperation.GetTask, "while waiting to poll GetTask");
                await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
                return;
            }

            try
            {
                var reply = await Client.GetTaskAsync(taskId, Options.HistoryLength, tokens.Token).ConfigureAwait(false);
                ctx.RecoveryRequests++;
                ctx.AddCapture(reply.Capture);
                ApplyTask(ctx, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture, reply.ReceivedAt);
            }
            catch (OperationCanceledException)
            {
                ctx.RecoveryRequests++;
                RecordLocalStop(ctx, tokens, callerToken, WorkIqOperation.GetTask, "during a GetTask poll");
                await TryCancelRemoteOnTimeoutAsync(ctx).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                ctx.RecoveryRequests++;
                var failure = WorkIqA2AClient.Classify(ex, WorkIqOperation.GetTask, requestId: null, taskId, ctx.ContextId, tokens.Token, Config.Scope) with
                {
                    Certainty = OutcomeCertainty.Indeterminate,
                    RemoteTaskState = current.RawState,
                    RpcRequestId = ctx.Identity.RpcRequestId,
                };
                if (failure.Category == WorkIqFailureCategory.NotFound)
                {
                    failure = failure with { SafeMessage = "GetTask: the task was not found during recovery (TaskUnavailable: inaccessible, expired or purged — not proof it never existed); the input was not resent" };
                }
                else if (failure.Category == WorkIqFailureCategory.Canceled)
                {
                    failure = RefineLocalStop(ctx, failure, tokens, callerToken, WorkIqOperation.GetTask, "during a GetTask poll");
                }

                if (failure.Capture is not null)
                {
                    ctx.AddCapture(failure.Capture);
                }

                ctx.Failure = failure;
                ctx.Transport = TransportOutcome.Interrupted;
                Diagnostics.Trace(WorkIqTraceStage.GetTask, "recovery.failed", $"recovery stopped after {ctx.RecoveryRequests} poll(s): {failure.Category} ({failure.Certainty}): {failure.SafeMessage}", WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: taskId, contextId: ctx.ContextId, requestId: failure.RequestId, httpStatus: failure.HttpStatus, rpcErrorCode: failure.RpcErrorCode, a2aState: current.RawState);
                return;
            }
        }

        Diagnostics.Trace(
            WorkIqTraceStage.GetTask,
            "recovery.finished",
            $"task reached {ctx.Snapshot?.RawState ?? "(absent)"} after {ctx.RecoveryRequests} GetTask poll(s)",
            caseId: ctx.Request.CaseId,
            turnId: ctx.Request.TurnId,
            taskId: taskId,
            contextId: ctx.ContextId,
            a2aState: ctx.Snapshot?.RawState);
    }

    private async Task ReconcileAfterStreamAsync(TurnContext ctx, TurnTokens tokens, CancellationToken callerToken, WorkIqOperation operation)
    {
        if (ctx.Failure is not null || ctx.Stopped)
        {
            return;
        }

        if (ctx.Snapshot is { State: WorkIqTaskState.Submitted or WorkIqTaskState.Working })
        {
            Diagnostics.Trace(StageOf(operation == WorkIqOperation.Subscribe ? WorkIqSnapshotSource.Subscribe : WorkIqSnapshotSource.Stream), "stream.ended_before_terminal", $"the {operation} stream ended without a terminal state; reconciling through GetTask (intermediate events may be missing)", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId, a2aState: ctx.Snapshot.RawState);
            await ReconcileAsync(ctx, tokens, callerToken).ConfigureAwait(false);
            if (ctx.Failure is null)
            {
                ctx.Transport = TransportOutcome.Recovered;
            }
        }
        else if (ctx.Snapshot is null && ctx.DirectMessage is null)
        {
            ctx.Failure = new WorkIqFailure(
                WorkIqFailureCategory.Transport,
                $"{operation}: the stream ended before a task id or a direct message arrived; the input was not resent",
                OutcomeCertainty.Indeterminate,
                operation,
                RpcRequestId: ctx.Identity.RpcRequestId,
                ContextId: ctx.SentContextId);
            ctx.Transport = TransportOutcome.Interrupted;
        }
    }

    private void RecordLocalStop(TurnContext ctx, TurnTokens tokens, CancellationToken callerToken, WorkIqOperation operation, string phase)
    {
        var baseline = new WorkIqFailure(
            WorkIqFailureCategory.Canceled,
            $"{operation}: the local wait stopped {phase}",
            OutcomeCertainty.Indeterminate,
            operation,
            RemoteTaskState: ctx.Snapshot?.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: ctx.Handle?.TaskId,
            ContextId: ctx.ContextId,
            Capture: ctx.LastCapture);
        ctx.Failure = RefineLocalStop(ctx, baseline, tokens, callerToken, operation, phase);
        ctx.Transport = TransportOutcome.Interrupted;
        Diagnostics.Trace(StageOf(operation == WorkIqOperation.Subscribe ? WorkIqSnapshotSource.Subscribe : operation == WorkIqOperation.Stream ? WorkIqSnapshotSource.Stream : WorkIqSnapshotSource.GetTask), "turn.stopped_locally", $"{ctx.Failure.Category}: {ctx.Failure.SafeMessage}", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId, a2aState: ctx.Snapshot?.RawState);
    }

    private async Task TryCancelRemoteOnTimeoutAsync(TurnContext ctx)
    {
        if (!Options.CancelRemoteTaskOnTimeout || ctx.Failure?.Category != WorkIqFailureCategory.Timeout || ctx.Handle is null)
        {
            return;
        }

        var taskId = ctx.Handle.TaskId;
        ClearPendingRpcId(ctx);
        Diagnostics.Trace(WorkIqTraceStage.Cancel, "cancel.requested", $"CancelRemoteTaskOnTimeout: sending CancelTask under its own {RemoteCancelTimeout} bound (the outcome is Canceled only if the service confirms it)", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: taskId, contextId: ctx.ContextId);
        using var bound = new CancellationTokenSource(RemoteCancelTimeout, Clock);
        try
        {
            var reply = await Client.CancelTaskAsync(taskId, bound.Token).ConfigureAwait(false);
            ctx.RecoveryRequests++;
            ctx.AddCapture(reply.Capture);
            ApplyTask(ctx, reply.Value, WorkIqSnapshotSource.GetTask, reply.Capture, reply.ReceivedAt);
            if (ctx.Snapshot is { IsTerminal: true } or { IsSuspended: true })
            {
                ctx.Transport = TransportOutcome.Recovered;
            }

            var confirmed = ctx.Snapshot?.State == WorkIqTaskState.Canceled;
            if (confirmed)
            {
                Volatile.Write(ref _confirmedCanceledTaskId, taskId);
            }

            Diagnostics.Trace(WorkIqTraceStage.Cancel, confirmed ? "cancel.confirmed" : "cancel.not_confirmed", confirmed ? "the service confirmed TASK_STATE_CANCELED" : $"the service replied with {ctx.Snapshot?.RawState ?? "(absent)"}: not canceled", confirmed ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: taskId, contextId: ctx.ContextId, requestId: reply.RequestId, httpStatus: reply.HttpStatus, a2aState: ctx.Snapshot?.RawState);
        }
        catch (Exception ex)
        {
            ctx.RecoveryRequests++;
            var failure = WorkIqA2AClient.Classify(ex, WorkIqOperation.CancelTask, requestId: null, taskId, ctx.ContextId, bound.Token, Config.Scope);
            if (failure.Capture is not null)
            {
                ctx.AddCapture(failure.Capture);
            }

            Diagnostics.Trace(WorkIqTraceStage.Cancel, "cancel.failed", $"CancelTask after timeout failed: {failure.Category}: {failure.SafeMessage}; the timeout outcome stands", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: taskId, contextId: ctx.ContextId, httpStatus: failure.HttpStatus, rpcErrorCode: failure.RpcErrorCode);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Stream frame reduction
    // ---------------------------------------------------------------------------------------------------------

    // The enumeration is driven by tokens.Token (caller + abandon + deadline, linked); the caller's own token is
    // read from tokens.Caller for stop attribution only, so this iterator deliberately takes no CancellationToken
    // parameter (CS8425) and no [EnumeratorCancellation] token that could silently diverge from tokens.Token.
    private async IAsyncEnumerable<WorkIqStreamEvent> ConsumeFramesAsync(
        TurnContext ctx,
        IAsyncEnumerable<WorkIqStreamFrame> frames,
        WorkIqSnapshotSource source,
        TurnTokens tokens)
    {
        var operation = source == WorkIqSnapshotSource.Subscribe ? WorkIqOperation.Subscribe : WorkIqOperation.Stream;
        var callerToken = tokens.Caller;
        var enumerator = frames.GetAsyncEnumerator(tokens.Token);
        try
        {
            var first = true;
            while (true)
            {
                WorkIqStreamFrame? frame;
                try
                {
                    frame = await enumerator.MoveNextAsync().ConfigureAwait(false) ? enumerator.Current : null;
                }
                catch (OperationCanceledException)
                {
                    RecordLocalStop(ctx, tokens, callerToken, operation, "while reading the stream");
                    yield break;
                }
                catch (Exception ex)
                {
                    var failure = WorkIqA2AClient.Classify(ex, operation, requestId: null, ctx.Handle?.TaskId, ctx.ContextId, tokens.Token, Config.Scope) with
                    {
                        RpcRequestId = ctx.Identity.RpcRequestId,
                        RemoteTaskState = ctx.Snapshot?.RawState,
                    };
                    if (failure.Category == WorkIqFailureCategory.Canceled)
                    {
                        failure = RefineLocalStop(ctx, failure, tokens, callerToken, operation, "while reading the stream");
                    }

                    if (failure.Capture is not null)
                    {
                        ctx.AddCapture(failure.Capture);
                    }

                    ctx.Failure = failure;
                    ctx.Transport = ctx.Snapshot is null && ctx.DirectMessage is null ? TransportOutcome.Failed : TransportOutcome.Interrupted;
                    ctx.Reducer?.MarkInterrupted();
                    Diagnostics.Trace(StageOf(source), "stream.failed", $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}", WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId, httpStatus: failure.HttpStatus, rpcErrorCode: failure.RpcErrorCode, a2aState: ctx.Snapshot?.RawState);
                    yield break;
                }

                if (frame is null)
                {
                    ctx.Reducer?.MarkStreamEnded();
                    yield break;
                }

                ctx.FirstEventAt ??= frame.ReceivedAt;
                foreach (var evt in ReduceFrame(ctx, frame, source, first))
                {
                    yield return evt;
                }

                first = false;
                if (ctx.Failure is not null || ctx.DirectMessage is not null || ctx.Snapshot is { IsTerminal: true } or { IsSuspended: true })
                {
                    yield break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private List<WorkIqStreamEvent> ReduceFrame(TurnContext ctx, WorkIqStreamFrame frame, WorkIqSnapshotSource source, bool isFirst)
    {
        var events = new List<WorkIqStreamEvent>();
        var turnId = ctx.Request.TurnId;
        var at = frame.ReceivedAt;
        var operation = source == WorkIqSnapshotSource.Subscribe ? WorkIqOperation.Subscribe : WorkIqOperation.Stream;
        var captureId = ctx.LastCapture?.CaptureId ?? ctx.Snapshot?.Capture?.CaptureId ?? UncapturedId;

        if (frame.Failure is { } frameFailure)
        {
            ctx.Failure = frameFailure with { RpcRequestId = frameFailure.RpcRequestId ?? ctx.Identity.RpcRequestId, TaskId = frameFailure.TaskId ?? ctx.Handle?.TaskId, ContextId = frameFailure.ContextId ?? ctx.ContextId };
            ctx.Transport = TransportOutcome.Failed;
            events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "stream.frame_failed", $"{frameFailure.Category}: {frameFailure.SafeMessage}", ImmutableArray<SourceRef>.Empty));
            Diagnostics.Trace(StageOf(source), "stream.frame_failed", $"frame {frame.ClientSequence} (epoch {frame.ConnectionEpoch}): {frameFailure.Category}: {frameFailure.SafeMessage}; the turn stops here (a malformed frame is never skipped)", WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: turnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId, rpcErrorCode: frameFailure.RpcErrorCode, httpStatus: frameFailure.HttpStatus);
            return events;
        }

        if (frame.Event is null)
        {
            events.Add(new WorkIqStreamEvent.UnknownProviderEvent(turnId, ctx.NextSequence(), at, ParseOrWrap(frame.DataJson), frame.SseEvent, frame.SseId));
            Diagnostics.Trace(StageOf(source), "stream.unknown_frame", $"frame {frame.ClientSequence} carries no recognised event (sse event {(frame.SseEvent is null ? "absent" : "present")}); retained raw", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: turnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId);
            return events;
        }

        var reducer = ctx.Reducer ??= CreateReducer(ctx);
        try
        {
            var response = frame.Event;
            switch (response.PayloadCase)
            {
                case StreamResponseCase.Task when response.Task is { } task:
                {
                    reducer.Load(task, source, frame);
                    var snapshot = RefreshSnapshot(ctx, source, at);
                    if (snapshot is null)
                    {
                        break;
                    }

                    events.Add(new WorkIqStreamEvent.TaskSnapshotObserved(turnId, ctx.NextSequence(), at, snapshot));
                    if (snapshot.IsSuspended)
                    {
                        events.Add(new WorkIqStreamEvent.InteractionRequired(turnId, ctx.NextSequence(), at, BuildPendingInteraction(snapshot, captureId)));
                    }

                    break;
                }

                case StreamResponseCase.Message when response.Message is { } message:
                {
                    reducer.Apply(frame, source);
                    ApplyDirectMessage(ctx, reducer.DirectMessage() ?? WorkIqSdkMapping.ToMessage(message), at);
                    break;
                }

                case StreamResponseCase.StatusUpdate when response.StatusUpdate is { } status:
                {
                    reducer.Apply(frame, source);
                    var snapshot = RefreshSnapshot(ctx, source, at);
                    if (snapshot is null)
                    {
                        events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "stream.status_without_task", "a statusUpdate arrived before any task snapshot established the task identity", ImmutableArray<SourceRef>.Empty));
                        break;
                    }

                    events.Add(new WorkIqStreamEvent.TaskStatusObserved(turnId, ctx.NextSequence(), at, snapshot.Handle, snapshot.State, snapshot.RawState, snapshot.StatusTimestamp, snapshot.StatusMessage));
                    if (!snapshot.IsTerminal && !snapshot.IsSuspended && snapshot.StatusMessage is { } progress)
                    {
                        events.Add(new WorkIqStreamEvent.ProgressObserved(turnId, ctx.NextSequence(), at, snapshot.Handle, progress));
                    }

                    if (snapshot.IsSuspended)
                    {
                        events.Add(new WorkIqStreamEvent.InteractionRequired(turnId, ctx.NextSequence(), at, BuildPendingInteraction(snapshot, captureId)));
                    }

                    _ = status;
                    break;
                }

                case StreamResponseCase.ArtifactUpdate when response.ArtifactUpdate is { } update:
                {
                    reducer.Apply(frame, source);
                    var snapshot = RefreshSnapshot(ctx, source, at);
                    var artifact = snapshot?.Artifacts.FirstOrDefault(a => string.Equals(a.ArtifactId, update.Artifact?.ArtifactId, StringComparison.Ordinal));
                    if (snapshot is null || artifact is null)
                    {
                        events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "stream.artifact_not_reduced", "an artifactUpdate could not be matched to a reduced artifact of the current task", ImmutableArray<SourceRef>.Empty));
                        break;
                    }

                    events.Add(new WorkIqStreamEvent.ArtifactRevisionObserved(turnId, ctx.NextSequence(), at, snapshot.Handle, artifact, update.Append, update.LastChunk));
                    break;
                }

                default:
                {
                    ctx.Failure = new WorkIqFailure(
                        WorkIqFailureCategory.Malformed,
                        $"{operation}: frame {frame.ClientSequence} carries none of task|message|statusUpdate|artifactUpdate (one-of violation)",
                        OutcomeCertainty.Known,
                        operation,
                        RpcRequestId: ctx.Identity.RpcRequestId,
                        TaskId: ctx.Handle?.TaskId,
                        ContextId: ctx.ContextId);
                    ctx.Transport = TransportOutcome.Failed;
                    events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "stream.one_of_violation", ctx.Failure.SafeMessage, ImmutableArray<SourceRef>.Empty));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = WorkIqA2AClient.Classify(ex, operation, requestId: null, ctx.Handle?.TaskId, ctx.ContextId) with
            {
                Certainty = OutcomeCertainty.Known,
                RpcRequestId = ctx.Identity.RpcRequestId,
                RemoteTaskState = ctx.Snapshot?.RawState,
            };
            if (failure.Category is WorkIqFailureCategory.Unknown or WorkIqFailureCategory.Canceled or WorkIqFailureCategory.Timeout)
            {
                failure = failure with { Category = WorkIqFailureCategory.Malformed, SafeMessage = $"{operation}: frame {frame.ClientSequence} could not be reduced ({ex.GetType().Name}); the turn stops here" };
            }

            ctx.Failure = failure;
            ctx.Transport = TransportOutcome.Failed;
            events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "reduce.failed", failure.SafeMessage, ImmutableArray<SourceRef>.Empty));
            Diagnostics.Trace(WorkIqTraceStage.Reduce, "reduce.failed", failure.SafeMessage, WorkIqTraceLevel.Error, caseId: ctx.Request.CaseId, turnId: turnId, taskId: ctx.Handle?.TaskId, contextId: ctx.ContextId);
        }

        for (var i = ctx.SchemaWarningsEmitted; i < (ctx.Snapshot?.SchemaWarnings.Length ?? 0); i++)
        {
            events.Add(new WorkIqStreamEvent.ProtocolWarning(turnId, ctx.NextSequence(), at, "reduce.schema_warning", ctx.Snapshot!.SchemaWarnings[i], ImmutableArray<SourceRef>.Empty));
        }

        ctx.SchemaWarningsEmitted = Math.Max(ctx.SchemaWarningsEmitted, ctx.Snapshot?.SchemaWarnings.Length ?? 0);
        _ = isFirst;
        return events;
    }

    private WorkIqTaskSnapshot? RefreshSnapshot(TurnContext ctx, WorkIqSnapshotSource source, DateTimeOffset at)
    {
        var snapshot = ctx.Reducer?.Snapshot();
        if (snapshot is null)
        {
            return null;
        }

        var handle = BuildHandle(ctx.Card!, snapshot.Handle.TaskId, snapshot.Handle.ContextId ?? ctx.ContextId);
        var adopted = snapshot with { Handle = handle };
        AdoptSnapshot(ctx, adopted, at, source);
        return adopted;
    }

    private static JsonElement ParseOrWrap(string? dataJson)
    {
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            try
            {
                using var document = JsonDocument.Parse(dataJson);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON: keep the text as a string element so nothing is lost.
            }
        }

        return JsonSerializer.SerializeToElement(dataJson ?? "");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Finalisation
    // ---------------------------------------------------------------------------------------------------------

    private WorkIqTurnResult FinalizeTurn(TurnContext ctx)
    {
        var endedAt = Clock.GetUtcNow();
        var snapshot = ctx.Snapshot;
        var direct = ctx.DirectMessage;

        if (snapshot is not null && ctx.Failure is null)
        {
            ctx.Failure = DescribeTerminalState(ctx, snapshot);
        }

        var (outcome, _) = WorkIqOutcomeClassifier.Classify(snapshot?.State, direct is not null, ctx.Transport, ctx.Failure, taskIdKnown: ctx.Handle is not null);

        var captureId = ctx.LastCapture?.CaptureId ?? snapshot?.Capture?.CaptureId ?? UncapturedId;
        var selection = direct is not null
            ? WorkIqAnswerSelector.SelectFromMessage(direct, captureId)
            : snapshot is not null
                ? WorkIqAnswerSelector.SelectFromTask(snapshot, captureId)
                : new WorkIqAnswerSelection(null, OutputCompleteness.None, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);

        var completeness = WorkIqOutcomeClassifier.AnswerCompletenessFor(outcome, selection);
        var answerText = outcome == WorkIqTurnOutcome.Completed && completeness is OutputCompleteness.Complete or OutputCompleteness.Empty
            ? selection.RawText
            : null;

        if (completeness == OutputCompleteness.Ambiguous)
        {
            Diagnostics.Trace(WorkIqTraceStage.Reduce, "answer.ambiguous", $"{selection.CandidateArtifactIds.Length} answer candidates; none was chosen and all are retained", WorkIqTraceLevel.Warning, caseId: ctx.Request.CaseId, turnId: ctx.Request.TurnId, taskId: snapshot?.Handle.TaskId, contextId: ctx.ContextId);
            Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Reduce, WorkIqIssueCategory.Protocol, "answer.ambiguous", $"The completed task carries {selection.CandidateArtifactIds.Length} answer candidates; no text was projected.", "The service returned several non-diagnostic artifacts without a single 'Answer'; evaluate the structured artifacts or extend the selection policy.") with { CaseId = ctx.Request.CaseId, TurnId = ctx.Request.TurnId, TaskId = snapshot?.Handle.TaskId, ContextId = ctx.ContextId }, WorkIqTraceLevel.Warning);
        }

        var diagnosticsObserved = snapshot is not null && snapshot.Artifacts.Any(WorkIqAnswerSelector.IsDiagnosticArtifact);
        var telemetry = WorkIqOutcomeClassifier.TelemetryFor(Profile, diagnosticsObserved);
        var pending = outcome is WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired && snapshot is not null
            ? BuildPendingInteraction(snapshot, captureId)
            : null;
        var captures = ctx.Captures.ToImmutableArray();
        var captureState = ComputeCaptureState(captures, ctx);
        var identity = ctx.Identity with
        {
            ProviderMessageId = direct?.MessageId ?? snapshot?.StatusMessage?.MessageId,
            ProviderTaskId = snapshot?.Handle.TaskId,
            ProviderContextId = ctx.ContextId,
        };

        Microsoft365TurnEvidence evidence;
        using (var scope = Diagnostics.Measure(WorkIqTraceStage.Normalize, "normalize", "evidence normalisation", ctx.Request.CaseId, ctx.Request.TurnId, snapshot?.Handle.TaskId, ctx.ContextId))
        {
            try
            {
                evidence = new WorkIqEvidenceNormalizer().Normalize(snapshot, direct, captures, captureState, Profile, identity);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Normalize, WorkIqIssueCategory.Unknown, "normalize.failed", $"The evidence normaliser threw {ex.GetType().Name}; the turn result could not be built (outcome would have been {outcome}).", "A normaliser defect or an unexpected payload shape; the trace carries the task state and ids, and the raw capture shows what arrived.") with { CaseId = ctx.Request.CaseId, TurnId = ctx.Request.TurnId, TaskId = snapshot?.Handle.TaskId, ContextId = ctx.ContextId, A2AState = snapshot?.RawState });
                throw;
            }
        }

        var timing = new WorkIqTurnTiming(ctx.StartedAt, ctx.FirstEventAt, ctx.FirstAnswerAt, ctx.TerminalAt, endedAt, ctx.RecoveryRequests);
        var result = new WorkIqTurnResult(
            outcome,
            ctx.Transport,
            completeness,
            telemetry,
            snapshot?.Handle,
            ctx.ContextId,
            answerText,
            evidence,
            pending,
            outcome == WorkIqTurnOutcome.Completed ? null : ctx.Failure,
            timing,
            identity);

        UpdateStateAfterTurn(ctx, result);
        TraceOutcome(ctx, result);
        return result;
    }

    private WorkIqFailure? DescribeTerminalState(TurnContext ctx, WorkIqTaskSnapshot snapshot)
    {
        var operation = snapshot.Source switch
        {
            WorkIqSnapshotSource.GetTask => WorkIqOperation.GetTask,
            WorkIqSnapshotSource.Stream => WorkIqOperation.Stream,
            WorkIqSnapshotSource.Subscribe => WorkIqOperation.Subscribe,
            _ => WorkIqOperation.Send,
        };
        var raw = snapshot.RawState ?? "(absent)";
        return snapshot.State switch
        {
            WorkIqTaskState.Completed or WorkIqTaskState.InputRequired or WorkIqTaskState.AuthRequired => null,
            WorkIqTaskState.Failed => Terminal(WorkIqFailureCategory.RemoteError, $"the task ended {raw}; partial artifacts are retained but are not an answer"),
            WorkIqTaskState.Rejected => Terminal(WorkIqFailureCategory.RemoteError, $"the service rejected the task ({raw})"),
            WorkIqTaskState.Canceled => Terminal(WorkIqFailureCategory.RemoteError, $"the service confirmed cancellation ({raw})"),
            WorkIqTaskState.Submitted or WorkIqTaskState.Working => new WorkIqFailure(
                WorkIqFailureCategory.Timeout,
                $"the task was still {raw} when the local operation ended; the input was not resent",
                OutcomeCertainty.Indeterminate,
                operation,
                RemoteTaskState: snapshot.RawState,
                RpcRequestId: ctx.Identity.RpcRequestId,
                TaskId: snapshot.Handle.TaskId,
                ContextId: snapshot.Handle.ContextId,
                Capture: snapshot.Capture),
            _ => new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                $"unrecognised task state '{raw}'; it never implies completion",
                OutcomeCertainty.Known,
                operation,
                RemoteTaskState: snapshot.RawState,
                RpcRequestId: ctx.Identity.RpcRequestId,
                TaskId: snapshot.Handle.TaskId,
                ContextId: snapshot.Handle.ContextId,
                Capture: snapshot.Capture),
        };

        WorkIqFailure Terminal(WorkIqFailureCategory category, string message) => new(
            category,
            $"{operation}: {message}",
            OutcomeCertainty.Known,
            operation,
            RemoteTaskState: snapshot.RawState,
            RpcRequestId: ctx.Identity.RpcRequestId,
            TaskId: snapshot.Handle.TaskId,
            ContextId: snapshot.Handle.ContextId,
            Capture: snapshot.Capture);
    }

    private static CaptureState ComputeCaptureState(ImmutableArray<RawCaptureRef> captures, TurnContext ctx)
    {
        if (captures.Any(c => c.State == CaptureState.Truncated))
        {
            return CaptureState.Truncated;
        }

        if (captures.Any(c => c.State == CaptureState.Interrupted) || ctx.Transport == TransportOutcome.Interrupted)
        {
            return CaptureState.Interrupted;
        }

        if (captures.IsEmpty || captures.All(c => c.State == CaptureState.NotRetained))
        {
            return CaptureState.NotRetained;
        }

        return CaptureState.Complete;
    }

    private void UpdateStateAfterTurn(TurnContext ctx, WorkIqTurnResult result)
    {
        lock (_stateGate)
        {
            if (ctx.Epoch != _epoch)
            {
                return; // The context was reset or abandoned while this turn ran: it must not resurrect it.
            }

            if (ctx.ContextId is not null)
            {
                _contextId = ctx.ContextId;
            }

            if (result.Task is not null)
            {
                _lastTask = result.Task;
            }

            var addressable = ctx.Snapshot is { } s && (s.State is WorkIqTaskState.Submitted or WorkIqTaskState.Working || s.IsSuspended);
            _activeTask = addressable ? result.Task : null;
            _lastOutcome = result.Outcome;
            if (ctx.LastCapture is not null)
            {
                _lastCapture = ctx.LastCapture;
            }
        }
    }

    private void TraceOutcome(TurnContext ctx, WorkIqTurnResult result)
    {
        var level = result.Outcome switch
        {
            WorkIqTurnOutcome.Completed => WorkIqTraceLevel.Info,
            WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired => WorkIqTraceLevel.Warning,
            _ => WorkIqTraceLevel.Error,
        };
        var message =
            $"outcome {result.Outcome}; transport {result.Transport}; answer {result.AnswerCompleteness} ({result.AnswerText?.Length ?? 0} chars); telemetry {result.Telemetry}; " +
            $"{result.Timing.RecoveryRequests} recovery request(s); {result.Timing.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms" +
            (result.Failure is null ? "" : $"; failure {result.Failure.Category} ({result.Failure.Certainty}): {result.Failure.SafeMessage}") +
            (result.PendingInteraction is null ? "" : $"; pending {result.PendingInteraction.Kind} with {result.PendingInteraction.ProposedCalls.Length} proposed call(s)");
        Diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Outcome, level, "turn.outcome", message)
        {
            CaseId = ctx.Request.CaseId,
            TurnId = ctx.Request.TurnId,
            RpcId = ctx.Identity.RpcRequestId,
            MessageId = ctx.Identity.ClientMessageId,
            TaskId = result.Task?.TaskId,
            ContextId = result.ContextId,
            RequestId = result.Failure?.RequestId,
            HttpStatus = result.Failure?.HttpStatus,
            RpcErrorCode = result.Failure?.RpcErrorCode,
            A2AState = ctx.Snapshot?.RawState,
            ElapsedMs = result.Timing.Elapsed.TotalMilliseconds,
        });

        if (result.Outcome == WorkIqTurnOutcome.Completed)
        {
            return;
        }

        var category = result.Failure is { } f ? WorkIqA2AClient.IssueCategoryOf(f.Category) ?? WorkIqIssueCategory.Service : WorkIqIssueCategory.Service;
        var cause = result.Outcome switch
        {
            WorkIqTurnOutcome.InputRequired => "The agent paused for confirmation or input; nothing was approved automatically. Use the rich API (ContinueAsync, wave 5) or rephrase the case so no action is proposed.",
            WorkIqTurnOutcome.AuthRequired => "The agent needs an external connector sign-in that this provider cannot complete; sign in to the connector in Microsoft 365 first.",
            WorkIqTurnOutcome.Rejected => "The service refused the task (policy, licence or content); see the status message in the capture.",
            WorkIqTurnOutcome.Canceled => "The service confirmed cancellation of the task.",
            WorkIqTurnOutcome.Failed => result.Failure is { } ff ? WorkIqA2AClient.SuggestedCauseOf(ff.Category) : "The task ended failed on the service side.",
            _ => result.Failure is { } fi ? WorkIqA2AClient.SuggestedCauseOf(fi.Category) : "The remote state is unknown; the task handle on the result can be read with GetTask.",
        };
        Diagnostics.Issue(new WorkIqIssue(default, WorkIqTraceStage.Outcome, category, "turn." + result.Outcome.ToString().ToLowerInvariant(), message, cause)
        {
            CaseId = ctx.Request.CaseId,
            TurnId = ctx.Request.TurnId,
            TaskId = result.Task?.TaskId,
            ContextId = result.ContextId,
            RequestId = result.Failure?.RequestId,
            HttpStatus = result.Failure?.HttpStatus,
            RpcErrorCode = result.Failure?.RpcErrorCode,
            A2AState = ctx.Snapshot?.RawState,
        }, level);
    }

    private void ThrowIfStoppedLocally(TurnContext ctx, WorkIqTurnResult result, CancellationToken callerToken)
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

    // ---------------------------------------------------------------------------------------------------------
    // Pending interactions (the pause payload of a suspended task)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the host-facing pause of a suspended task: <see cref="InteractionDecoder.TryDecode"/> reads the
    /// interaction data part of the status message (a confirmation card with every proposed call — multiplicity
    /// preserved, only byte-identical <c>action</c>/<c>actionId</c> twins merged — and the exact
    /// <c>confirmationOption</c> strings; an <c>auth-required</c> payload; an unrecognised payload retained raw);
    /// the content policy is applied here (<see cref="EvidenceContentPolicy.MetadataOnly"/> drops the prompt text
    /// and the raw payload); a text-only pause without an interaction part becomes a plain
    /// <see cref="WorkIqInteractionKind.InputRequired"/> or <see cref="WorkIqInteractionKind.AuthenticationRequired"/>
    /// pause by task state. Nothing is answered.
    /// </summary>
    /// <param name="snapshot">The suspended task.</param>
    /// <param name="captureId">The capture the snapshot came from (source pointers).</param>
    internal WorkIqPendingInteraction BuildPendingInteraction(WorkIqTaskSnapshot snapshot, string captureId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var retainContent = Options.ContentPolicy != EvidenceContentPolicy.MetadataOnly;
        var decoded = snapshot.StatusMessage is { } message ? InteractionDecoder.TryDecode(message, snapshot.Handle, captureId) : null;

        if (decoded is null)
        {
            var kind = snapshot.State == WorkIqTaskState.AuthRequired ? WorkIqInteractionKind.AuthenticationRequired : WorkIqInteractionKind.InputRequired;
            string? prompt = null;
            if (retainContent && snapshot.StatusMessage is { } textMessage)
            {
                var text = string.Concat(textMessage.Parts.Where(p => p.Kind == WorkIqPartKind.Text && p.Text is not null).Select(p => p.Text));
                prompt = text.Length == 0 ? null : text;
            }

            Diagnostics.Trace(
                WorkIqTraceStage.Reduce,
                "interaction.text_only",
                $"the {snapshot.RawState ?? "(absent)"} pause carries no interaction data part; surfaced as {kind} with no options and no proposed calls",
                WorkIqTraceLevel.Warning,
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
            return new WorkIqPendingInteraction(
                snapshot.Handle,
                kind,
                null,
                prompt,
                ImmutableArray<WorkIqProposedCall>.Empty,
                ImmutableArray<string>.Empty,
                InteractionProfileId,
                null,
                ImmutableArray<SourceRef>.Empty);
        }

        if (decoded.Kind == WorkIqInteractionKind.Unknown)
        {
            Diagnostics.Trace(
                WorkIqTraceStage.Reduce,
                "interaction.unrecognised",
                $"the pause payload is not recognised by {decoded.ProfileId}; retained raw, never answered",
                WorkIqTraceLevel.Warning,
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }
        else
        {
            Diagnostics.Trace(
                WorkIqTraceStage.Reduce,
                "interaction.decoded",
                $"pause {decoded.Kind}: {decoded.ProposedCalls.Length} proposed call(s), options {(decoded.RecognizedOptions.IsEmpty ? "none" : string.Join("/", decoded.RecognizedOptions))}, " +
                $"sourceRequestId {(decoded.SourceRequestId is null ? "absent" : "present")}",
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }

        return retainContent ? decoded : decoded with { PromptText = null, RawInteractionData = null };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Handles, principal, misc
    // ---------------------------------------------------------------------------------------------------------

    private WorkIqTaskHandle BuildHandle(RawAgentCard card, string taskId, string? contextId) =>
        new(
            card.SelectedEndpoint,
            card.SelectedProtocolVersion,
            string.IsNullOrWhiteSpace(Config.AgentId) ? null : Config.AgentId.Trim(),
            taskId,
            string.IsNullOrWhiteSpace(contextId) ? null : contextId,
            RoutingTenantOf(card),
            PrincipalKey);

    private static string? RoutingTenantOf(RawAgentCard card)
    {
        foreach (var candidate in card.Card.SupportedInterfaces ?? [])
        {
            if (candidate is not null
                && Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url)
                && url == card.SelectedEndpoint
                && !string.IsNullOrWhiteSpace(candidate.Tenant))
            {
                return candidate.Tenant;
            }
        }

        return null;
    }

    private WorkIqTaskSnapshot SnapshotOf(AgentTask task, WorkIqSnapshotSource source, RawCaptureRef capture)
    {
        var reducer = CreateReducer(Card!, caseId: null, turnId: null);
        reducer.Load(task, source, null, capture);
        var snapshot = reducer.Snapshot() ?? throw new InvalidOperationException("The task reducer produced no snapshot after loading a task.");
        return snapshot with { Handle = BuildHandle(Card!, task.Id, task.ContextId), Capture = capture };
    }

    private void RecordExplicitRead(WorkIqTaskSnapshot snapshot, RawCaptureRef capture)
    {
        if (snapshot.State == WorkIqTaskState.Canceled)
        {
            Volatile.Write(ref _confirmedCanceledTaskId, snapshot.Handle.TaskId);
        }

        lock (_stateGate)
        {
            _lastCapture = capture;
            if (_activeTask is not null && string.Equals(_activeTask.TaskId, snapshot.Handle.TaskId, StringComparison.Ordinal) && snapshot.IsTerminal)
            {
                _activeTask = null;
            }
        }
    }

    private void ValidateHandle(WorkIqTaskHandle handle, RawAgentCard card)
    {
        if (handle.Endpoint != card.SelectedEndpoint)
        {
            throw new InvalidOperationException(
                $"The task handle belongs to another endpoint ({handle.Endpoint.Host}{handle.Endpoint.AbsolutePath}); this conversation talks to {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath}.");
        }

        if (!string.Equals(handle.PrincipalKey, PrincipalKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The task handle belongs to another principal (tenant, app or scope); it cannot be read through this conversation.");
        }
    }

    private WorkIqTaskReducer CreateReducer(TurnContext ctx) =>
        CreateReducer(ctx.Card ?? throw new InvalidOperationException("A reducer needs the discovered card."), ctx.Request.CaseId, ctx.Request.TurnId);

    /// <summary>A reducer whose handle template carries this conversation's endpoint, version, agent, routing tenant and principal (task id blank until the first snapshot).</summary>
    private WorkIqTaskReducer CreateReducer(RawAgentCard card, string? caseId, string? turnId) =>
        new(BuildHandle(card, "", null), Clock, Diagnostics, caseId, turnId);

    /// <summary>Attaches the capture of the stream that just ended (known only then) to the turn and its reducer.</summary>
    private void AttachStreamCapture(TurnContext ctx)
    {
        if (Client is not WorkIqA2AClient concrete || concrete.LastStreamCapture is not { } capture)
        {
            return;
        }

        ctx.AddCapture(capture);
        ctx.Reducer?.AttachCapture(capture);
        if (ctx.Snapshot is { Capture: null } snapshot)
        {
            ctx.Snapshot = snapshot with { Capture = capture };
        }
    }

    private static bool HasAnswerText(WorkIqTaskSnapshot snapshot)
    {
        foreach (var artifact in snapshot.Artifacts)
        {
            if (WorkIqAnswerSelector.IsDiagnosticArtifact(artifact))
            {
                continue;
            }

            if (artifact.Parts.Any(p => p.Kind == WorkIqPartKind.Text && !string.IsNullOrEmpty(p.Text)))
            {
                return true;
            }
        }

        return false;
    }

    private static WorkIqTraceStage StageOf(WorkIqSnapshotSource source) => source switch
    {
        WorkIqSnapshotSource.GetTask => WorkIqTraceStage.GetTask,
        WorkIqSnapshotSource.Stream => WorkIqTraceStage.Stream,
        WorkIqSnapshotSource.Subscribe => WorkIqTraceStage.Subscribe,
        _ => WorkIqTraceStage.Send,
    };

    private static string NewRpcId() => "rpc-" + Guid.NewGuid().ToString("N");

    private static string ComputePrincipalKey(string tenantId, string appClientId, string scope)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId.Trim()}|{appClientId.Trim()}|{scope.Trim()}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private TurnTokens CreateTurnTokens(CancellationToken callerToken)
    {
        var abandon = _abandonCts.Token;
        var deadline = Options.TurnDeadline > TimeSpan.Zero ? new CancellationTokenSource(Options.TurnDeadline, Clock) : null;
        var linked = deadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(callerToken, abandon)
            : CancellationTokenSource.CreateLinkedTokenSource(callerToken, abandon, deadline.Token);
        return new TurnTokens(linked, deadline, abandon, callerToken);
    }

    private void AcquireTurnGate()
    {
        if (!_turnGate.Wait(0))
        {
            throw new InvalidOperationException("one active turn per conversation: a turn or subscription already owns this conversation; await it or call AbandonAndResetAsync first.");
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

    private const string UncapturedId = "uncaptured";

    private static readonly JsonSerializerOptions LocationJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LocationMetadata(string TimeZone, int TimeZoneOffset);

    /// <summary>The cancellation sources of one turn: the caller's token, the abandonment token and the optional deadline, linked.</summary>
    private sealed class TurnTokens : IDisposable
    {
        private readonly CancellationTokenSource _linked;
        private readonly CancellationTokenSource? _deadline;

        public TurnTokens(CancellationTokenSource linked, CancellationTokenSource? deadline, CancellationToken abandon, CancellationToken caller = default)
        {
            _linked = linked;
            _deadline = deadline;
            Abandon = abandon;
            Caller = caller;
        }

        public CancellationToken Token => _linked.Token;

        public CancellationToken Abandon { get; }

        /// <summary>The caller's own token, kept apart from <see cref="Token"/> so a local stop can be attributed to the caller rather than the budget.</summary>
        public CancellationToken Caller { get; }

        public bool DeadlineFired => _deadline?.IsCancellationRequested == true;

        public void Dispose()
        {
            _linked.Dispose();
            _deadline?.Dispose();
        }
    }

    /// <summary>Everything one turn (or subscription) accumulates before its result is built.</summary>
    private sealed class TurnContext
    {
        private long _sequence;

        public TurnContext(WorkIqTurnRequest request, TurnIdentity identity, WorkIqOperation operation, DateTimeOffset startedAt, int epoch, CancellationToken abandonToken)
        {
            Request = request;
            Identity = identity;
            Operation = operation;
            StartedAt = startedAt;
            Epoch = epoch;
            AbandonToken = abandonToken;
        }

        public WorkIqTurnRequest Request { get; }

        public TurnIdentity Identity { get; }

        public WorkIqOperation Operation { get; }

        public DateTimeOffset StartedAt { get; }

        public int Epoch { get; }

        public CancellationToken AbandonToken { get; }

        public RawAgentCard? Card { get; set; }

        public DateTimeOffset? FirstEventAt { get; set; }

        public DateTimeOffset? FirstAnswerAt { get; set; }

        public DateTimeOffset? TerminalAt { get; set; }

        public int RecoveryRequests { get; set; }

        public List<RawCaptureRef> Captures { get; } = [];

        public RawCaptureRef? LastCapture { get; private set; }

        public WorkIqTaskReducer? Reducer { get; set; }

        public WorkIqTaskSnapshot? Snapshot { get; set; }

        public WorkIqMessage? DirectMessage { get; set; }

        public WorkIqTaskHandle? Handle { get; set; }

        public string? ContextId { get; set; }

        public string? SentContextId { get; set; }

        public WorkIqFailure? Failure { get; set; }

        public TransportOutcome Transport { get; set; } = TransportOutcome.Completed;

        public bool CanceledByCaller { get; set; }

        public bool Abandoned { get; set; }

        public bool DeadlineFired { get; set; }

        public bool Stopped => CanceledByCaller || Abandoned || DeadlineFired;

        public int SchemaWarningsTraced { get; set; }

        public int SchemaWarningsEmitted { get; set; }

        public long NextSequence() => _sequence++;

        public void AddCapture(RawCaptureRef capture)
        {
            if (!Captures.Any(c => ReferenceEquals(c, capture) || string.Equals(c.CaptureId, capture.CaptureId, StringComparison.Ordinal)))
            {
                Captures.Add(capture);
            }

            LastCapture = capture;
        }
    }
}
