// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Builds what a turn sends and runs the blocking send. The request is the user message, the context it continues,
/// the optional <c>Location</c> metadata and the send configuration. The dispatch is traced once, and the reply is
/// applied to the turn. The input is sent once and never resent.
/// </summary>
internal sealed class TurnSender
{
    private static readonly JsonSerializerOptions LocationJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWorkIqA2AClient _client;
    private readonly Microsoft365Config _config;
    private readonly WorkIqOptions _options;
    private readonly DiagnosticProfile _profile;
    private readonly ConversationState _state;
    private readonly TurnObserver _observer;
    private readonly TurnFailures _failures;

    public TurnSender(
        IWorkIqA2AClient client,
        Microsoft365Config config,
        WorkIqOptions options,
        DiagnosticProfile profile,
        ConversationState state,
        TurnObserver observer,
        TurnFailures failures)
    {
        _client = client;
        _config = config;
        _options = options;
        _profile = profile;
        _state = state;
        _observer = observer;
        _failures = failures;
    }

    /// <summary>Builds the turn's send request, records the context it continues, and traces its dispatch.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="method">The JSON-RPC method, for the trace.</param>
    public SendMessageRequest Dispatch(TurnContext ctx, string method)
    {
        var request = BuildRequest(ctx);
        ctx.SentContextId = request.Message.ContextId;
        TraceDispatch(ctx, request, method);
        return request;
    }

    /// <summary>Sends the turn once and applies the reply: a task, a direct message, or a failure.</summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="tokens">The turn's tokens.</param>
    public async Task SendBlockingAsync(TurnContext ctx, TurnTokens tokens)
    {
        var request = Dispatch(ctx, "SendMessage");
        WorkIqRpcResult<SendMessageResponse> reply;
        try
        {
            // The send carries the turn identity's JSON-RPC id, so the identity names the id that went on the wire.
            reply = await _client.SendAsync(request, tokens.Token, ctx.Call(ctx.Identity.RpcRequestId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _failures.RecordSendFailure(ctx, ex, tokens, WorkIqOperation.Send);
            return;
        }

        ctx.FirstEventAt ??= reply.ReceivedAt;
        ctx.AddCapture(reply.Capture);

        if (reply.Value.Task is { } task)
        {
            _observer.ApplyTask(ctx, task, WorkIqSnapshotSource.Send, reply.Capture, reply.ReceivedAt);
        }
        else if (reply.Value.Message is { } message)
        {
            TurnObserver.ApplyDirectMessage(ctx, WorkIqSdkMapping.ToMessage(message), reply.ReceivedAt);
        }
        else
        {
            // The service answered the send, so a task may exist: the remote state is unknown.
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                "Send: the reply carries neither a task nor a message",
                OutcomeCertainty.Indeterminate,
                WorkIqOperation.Send,
                HttpStatus: reply.HttpStatus,
                RequestId: reply.RequestId,
                RpcRequestId: ctx.Identity.RpcRequestId,
                Capture: reply.Capture);
            ctx.Fail(failure, TransportOutcome.Failed);
        }
    }

    /// <summary>
    /// The <c>Location</c> metadata of a user message when <see cref="Microsoft365Config.TimeZone"/> is set: the IANA
    /// id and the DST-aware offset (minutes) of that zone at <paramref name="instant"/>. Null when no zone is set.
    /// </summary>
    /// <param name="instant">The evaluation reference instant (<see cref="WorkIqOptions.EvaluationReferenceInstant"/> or the send time).</param>
    public Dictionary<string, JsonElement>? BuildLocationMetadata(DateTimeOffset instant)
    {
        if (string.IsNullOrWhiteSpace(_config.TimeZone))
        {
            return null;
        }

        var zoneId = _config.TimeZone.Trim();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var offsetMinutes = (int)Math.Round(zone.GetUtcOffset(instant).TotalMinutes);
        var location = JsonSerializer.SerializeToElement(new LocationMetadata(zoneId, offsetMinutes), LocationJsonOptions);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["Location"] = location };
    }

    private SendMessageRequest BuildRequest(TurnContext ctx)
    {
        var reuse = _state.ContextToContinue(_options.ConversationPolicy);
        var message = new Message
        {
            MessageId = ctx.Identity.ClientMessageId,
            Role = Role.User,
            Parts = [Part.FromText(ctx.Request.Input)],
            ContextId = reuse,
            TaskId = null,
            Metadata = BuildLocationMetadata(_options.EvaluationReferenceInstant ?? ctx.StartedAt),
        };
        var request = new SendMessageRequest { Message = message };

        // This conversation's profile and history length (they may differ from the client's options). The client
        // fills in the routing tenant under the same rule.
        WorkIqA2AClient.ApplySendConfiguration(request, _profile, _options.HistoryLength, routingTenant: null);
        return request;
    }

    private static void TraceDispatch(TurnContext ctx, SendMessageRequest request, string method)
    {
        ctx.Diagnostics.Trace(new WorkIqTraceEvent(default, ctx.Operation == WorkIqOperation.Stream ? WorkIqTraceStage.Stream : WorkIqTraceStage.Send, WorkIqTraceLevel.Info, "send.dispatch",
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

    private sealed record LocationMetadata(string TimeZone, int TimeZoneOffset);
}
