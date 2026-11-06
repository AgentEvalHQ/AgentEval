// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// What differs between the two streamed operations of a conversation. A streamed turn sends the input once. A
/// subscription reads an existing task and never resends anything. The conversation runs both through one skeleton:
/// discovery, the turn tokens and the sign-in wait, the stream, the reconciliation and the result.
/// </summary>
internal abstract class StreamingTurn
{
    /// <summary>The operation: <see cref="WorkIqOperation.Stream"/> or <see cref="WorkIqOperation.Subscribe"/>.</summary>
    public abstract WorkIqOperation Operation { get; }

    /// <summary>Where the stream's snapshots come from.</summary>
    public WorkIqSnapshotSource Source => Operation == WorkIqOperation.Subscribe ? WorkIqSnapshotSource.Subscribe : WorkIqSnapshotSource.Stream;

    /// <summary>A streamed send of <paramref name="request"/>. A null request throws only when the enumeration starts.</summary>
    /// <param name="request">The caller's turn request.</param>
    /// <param name="sender">Builds and traces the send.</param>
    /// <param name="client">The transport.</param>
    public static StreamingTurn Send(WorkIqTurnRequest? request, TurnSender sender, IWorkIqA2AClient client) =>
        new StreamedSend(request, sender, client);

    /// <summary>A subscription to <paramref name="task"/>. A null task throws only when the enumeration starts.</summary>
    /// <param name="task">The task to subscribe to.</param>
    /// <param name="handles">Validates the handle.</param>
    /// <param name="client">The transport.</param>
    public static StreamingTurn Subscribe(WorkIqTaskHandle? task, TaskHandleFactory handles, IWorkIqA2AClient client) =>
        new Subscription(task, handles, client);

    /// <summary>Throws <see cref="ArgumentNullException"/> for a missing argument. It runs when the enumeration starts, as in any async iterator.</summary>
    public abstract void ThrowIfArgumentMissing();

    /// <summary>The turn request: the caller's for a streamed turn, a local one for a subscription.</summary>
    public abstract WorkIqTurnRequest CreateRequest();

    /// <summary>Sets what the turn knows before discovery. A subscription knows its task.</summary>
    /// <param name="ctx">The turn.</param>
    public virtual void Bind(TurnContext ctx)
    {
    }

    /// <summary>Checks the turn against the discovered card before any call. A subscription checks its handle.</summary>
    /// <param name="ctx">The turn.</param>
    public virtual void Validate(TurnContext ctx)
    {
    }

    /// <summary>
    /// Builds and traces what goes out, and returns the call that opens the stream. The caller makes that call under
    /// the send-failure guard.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="tokens">The turn's tokens.</param>
    /// <param name="call">The stream's call context.</param>
    public abstract Func<IAsyncEnumerable<WorkIqStreamFrame>> Dispatch(TurnContext ctx, TurnTokens tokens, WorkIqCallContext call);

    /// <summary>Checks the turn once the stream ended, before the reconciliation. A subscription needs a first snapshot.</summary>
    /// <param name="ctx">The turn.</param>
    public virtual void CheckStreamEnd(TurnContext ctx)
    {
    }

    private sealed class StreamedSend : StreamingTurn
    {
        private readonly WorkIqTurnRequest? _request;
        private readonly TurnSender _sender;
        private readonly IWorkIqA2AClient _client;

        public StreamedSend(WorkIqTurnRequest? request, TurnSender sender, IWorkIqA2AClient client)
        {
            _request = request;
            _sender = sender;
            _client = client;
        }

        public override WorkIqOperation Operation => WorkIqOperation.Stream;

        // The name of the public method's parameter.
        public override void ThrowIfArgumentMissing() => ArgumentNullException.ThrowIfNull(_request, "request");

        public override WorkIqTurnRequest CreateRequest() => _request!;

        public override Func<IAsyncEnumerable<WorkIqStreamFrame>> Dispatch(TurnContext ctx, TurnTokens tokens, WorkIqCallContext call)
        {
            var sendRequest = _sender.Dispatch(ctx, "SendStreamingMessage");

            // The SDK mints the stream's JSON-RPC id itself, so the call carries none.
            return () => _client.StreamAsync(sendRequest, tokens.Token, call);
        }
    }

    private sealed class Subscription : StreamingTurn
    {
        private readonly WorkIqTaskHandle? _task;
        private readonly TaskHandleFactory _handles;
        private readonly IWorkIqA2AClient _client;

        public Subscription(WorkIqTaskHandle? task, TaskHandleFactory handles, IWorkIqA2AClient client)
        {
            _task = task;
            _handles = handles;
            _client = client;
        }

        public override WorkIqOperation Operation => WorkIqOperation.Subscribe;

        private WorkIqTaskHandle Handle => _task!;

        // The name of the public method's parameter.
        public override void ThrowIfArgumentMissing() => ArgumentNullException.ThrowIfNull(_task, "task");

        public override WorkIqTurnRequest CreateRequest() =>
            new("", "subscribe:" + Handle.TaskId, "subscribe-" + Guid.NewGuid().ToString("N"));

        public override void Bind(TurnContext ctx)
        {
            ctx.Handle = Handle;
            ctx.ContextId = Handle.ContextId;
        }

        public override void Validate(TurnContext ctx) => _handles.Validate(Handle, ctx.Card!);

        public override Func<IAsyncEnumerable<WorkIqStreamFrame>> Dispatch(TurnContext ctx, TurnTokens tokens, WorkIqCallContext call)
        {
            ctx.Trace(
                WorkIqTraceStage.Subscribe,
                "subscribe.dispatch",
                "SubscribeToTask dispatched (never resubmits input; the first frame must be a task snapshot)",
                taskId: Handle.TaskId,
                contextId: Handle.ContextId);
            return () => _client.SubscribeAsync(Handle.TaskId, tokens.Token, call);
        }

        public override void CheckStreamEnd(TurnContext ctx)
        {
            if (ctx.Snapshot is not null || ctx.DirectMessage is not null || ctx.Failure is not null)
            {
                return;
            }

            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                "Subscribe: the subscription ended without a first task snapshot (protocol/service deviation); local state was not replaced",
                OutcomeCertainty.Indeterminate,
                WorkIqOperation.Subscribe,
                TaskId: Handle.TaskId,
                ContextId: Handle.ContextId,
                RpcRequestId: ctx.Identity.RpcRequestId);
            ctx.Fail(failure, TransportOutcome.Failed);
        }
    }
}
