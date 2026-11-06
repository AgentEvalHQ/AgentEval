// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The provider's seam over the A2A .NET SDK (1.0.0-preview2): authenticated discovery with the trust policy,
/// exact request/response transport, correlation ids (<c>request-id</c> header, RPC id), raw capture references
/// and transport error classification. It never interprets tool meaning, scores, consents, or resubmits a send;
/// a 429/503 backoff (with jitter) applies only to discovery, <c>GetTask</c> and <c>CancelTask</c>. Every
/// response is validated as A2A 1.0; a 0.3-shaped reply fails with
/// <see cref="WorkIqFailureCategory.ProtocolVersionMismatch"/>. Every operation takes an optional
/// <see cref="WorkIqCallContext"/>: the per-call facts travel with the call, never in fields of the client.
/// </summary>
internal interface IWorkIqA2AClient
{
    /// <summary>
    /// Fetches the agent card (configured card URI, <c>A2A-Version: 1.0</c>, bearer only because the card host is
    /// trusted), validates it, selects the JSON-RPC interface with protocol version 1.0 on a trusted host, and
    /// returns the typed card with its raw JSON and digest.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context; null means a call of its own (no turn).</param>
    Task<RawAgentCard> DiscoverAsync(CancellationToken cancellationToken, WorkIqCallContext? call = null);

    /// <summary>Blocking <c>SendMessage</c>; the reply is a task or a direct message. Never retried.</summary>
    /// <param name="request">The request (<c>returnImmediately</c> set explicitly by the caller).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context (the JSON-RPC id to send among it); null means a call of its own.</param>
    Task<WorkIqRpcResult<SendMessageResponse>> SendAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null);

    /// <summary>
    /// <c>SendStreamingMessage</c>; yields every SSE frame with its envelope and the typed event (or a frame-level
    /// failure). Never retried. When the stream ends, its raw capture is on <see cref="WorkIqCallContext.StreamCapture"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context; null means a call of its own.</param>
    IAsyncEnumerable<WorkIqStreamFrame> StreamAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null);

    /// <summary><c>GetTask</c>; the task is directly under <c>result</c>. Safe to retry under backoff.</summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="historyLength">The <c>historyLength</c> to request; null omits it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context; null means a call of its own.</param>
    Task<WorkIqRpcResult<AgentTask>> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken, WorkIqCallContext? call = null);

    /// <summary><c>CancelTask</c>; returns the task as the service reports it after the request (canceled only if its state says so).</summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context; null means a call of its own.</param>
    Task<WorkIqRpcResult<AgentTask>> CancelTaskAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null);

    /// <summary>
    /// <c>SubscribeToTask</c>; the first frame is the current task snapshot, then future events. Never resubmits input.
    /// When the stream ends, its raw capture is on <see cref="WorkIqCallContext.StreamCapture"/>.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="call">The per-call context; null means a call of its own.</param>
    IAsyncEnumerable<WorkIqStreamFrame> SubscribeAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null);
}

/// <summary>A discovered agent card together with the bytes it was parsed from and the interface the trust policy selected.</summary>
/// <param name="Card">The typed card.</param>
/// <param name="RawJson">The card JSON exactly as received.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of <paramref name="RawJson"/> (recorded in run provenance).</param>
/// <param name="CardUri">The URI the card was fetched from.</param>
/// <param name="SelectedEndpoint">The JSON-RPC interface URL selected by the trust policy.</param>
/// <param name="SelectedProtocolVersion">The protocol version of the selected interface (<c>1.0</c>).</param>
/// <param name="FetchedAt">Local fetch time.</param>
/// <param name="RoutingTenant">The opaque A2A routing <c>tenant</c> the selected interface declares, set once by discovery; null when it declares none.</param>
internal sealed record RawAgentCard(
    AgentCard Card,
    string RawJson,
    string Sha256,
    Uri CardUri,
    Uri SelectedEndpoint,
    string SelectedProtocolVersion,
    DateTimeOffset FetchedAt,
    string? RoutingTenant = null);

/// <summary>A typed RPC reply together with its raw capture and correlation facts.</summary>
/// <typeparam name="T">The SDK result type.</typeparam>
/// <param name="Value">The typed result.</param>
/// <param name="Capture">The raw capture of the exchange (request + response bytes under the content policy).</param>
/// <param name="RequestId">The service's <c>request-id</c> response header, when present.</param>
/// <param name="HttpStatus">The HTTP status code.</param>
/// <param name="ReceivedAt">Local receipt time of the response.</param>
internal sealed record WorkIqRpcResult<T>(
    T Value,
    RawCaptureRef Capture,
    string? RequestId,
    int HttpStatus,
    DateTimeOffset ReceivedAt);

/// <summary>
/// One SSE frame as received: its client sequence and connection epoch (client order, not server order), local
/// receipt time, the typed event when it parsed, the raw <c>data</c> JSON, the SSE envelope fields, and a
/// frame-level failure (JSON-RPC error, malformed payload, one-of violation) when it did not.
/// </summary>
/// <param name="ClientSequence">The frame's 1-based position within its own stream call: every <c>SendStreamingMessage</c> or <c>SubscribeToTask</c> call numbers its frames from 1 again (a failure frame included); order across calls comes from <paramref name="ConnectionEpoch"/>.</param>
/// <param name="ConnectionEpoch">Which stream call delivered the frame: 1-based, incremented by the client for every stream call (send or subscribe), so a reconnect's frames carry a higher epoch than the dropped stream's.</param>
/// <param name="ReceivedAt">Local receipt time.</param>
/// <param name="Event">The typed stream response, when the frame parsed and validated.</param>
/// <param name="DataJson">The raw <c>data</c> payload (joined multi-line), under the content policy.</param>
/// <param name="SseId">The SSE <c>id</c> field, when any (preserved; no replay contract is assumed).</param>
/// <param name="SseEvent">The SSE <c>event</c> field, when any.</param>
/// <param name="Failure">The frame-level failure, when the frame could not be used.</param>
internal sealed record WorkIqStreamFrame(
    long ClientSequence,
    int ConnectionEpoch,
    DateTimeOffset ReceivedAt,
    StreamResponse? Event,
    string? DataJson,
    string? SseId,
    string? SseEvent,
    WorkIqFailure? Failure);
