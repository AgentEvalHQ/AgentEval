// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The production <see cref="IWorkIqA2AClient"/> over the A2A .NET SDK types, built on an <see cref="HttpClient"/>
/// whose pipeline is <c>WorkIqAuthHandler → RawCaptureHandler → inner</c>. The blocking operations live here
/// (lane A, M10); the two streaming members live in <c>WorkIqA2AClient.Streaming.cs</c> (lane C, M13).
/// </summary>
/// <remarks>
/// <para>
/// Discovery GETs the configured card URI through <see cref="Http"/> (the auth handler adds the bearer and the
/// <c>A2A-Version: 1.0</c> header; the capture handler tees the bytes), parses it with the SDK's JSON options and
/// keeps the raw JSON and its SHA-256, then lets <see cref="Policy"/> select the interface <b>before</b> any
/// request goes to it. The blocking <c>SendMessage</c>, <c>GetTask</c> and <c>CancelTask</c> calls post the JSON-RPC
/// envelope themselves (SDK <see cref="JsonRpcRequest"/> / <see cref="JsonRpcContent"/> / <see cref="JsonRpcResponse"/>
/// types, <see cref="A2AJsonUtilities.DefaultOptions"/>) rather than through <see cref="A2AClient"/>: the SDK client
/// mints its own RPC id, hides the HTTP status behind <c>EnsureSuccessStatusCode</c> and never exposes the
/// <c>request-id</c> header, all three of which this provider must record truthfully. <see cref="Client"/> is still
/// created on the selected endpoint for the streaming members.
/// </para>
/// <para>
/// A 429/503 reply is retried (bounded, exponential with jitter, <c>Retry-After</c> honoured) only for discovery,
/// <c>GetTask</c> and <c>CancelTask</c>; a send is never resent. Every reply is validated as A2A 1.0: a JSON-RPC
/// error on HTTP 200 and a 0.3-shaped body (<c>kind</c> discriminators, lowercase states) both fail the call.
/// </para>
/// </remarks>
internal sealed partial class WorkIqA2AClient : IWorkIqA2AClient
{
    /// <summary>How many times a 429/503 reply of a safe read is retried after the first attempt.</summary>
    internal const int MaxRetries = 3;

    /// <summary>The base of the exponential backoff (doubled per retry, plus jitter below one base).</summary>
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait honoured for a single retry, <c>Retry-After</c> included.</summary>
    internal static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>The media type of the native evaluator's <c>Developer Info</c> artifact (alias of <see cref="DeveloperInfoOutputMode"/>; observed, not a documented contract).</summary>
    internal const string DeveloperInfoMediaType = DeveloperInfoOutputMode;

    /// <summary>The media type of the native evaluator's <c>Retrieval Info</c> artifact (alias of <see cref="RetrievalInfoOutputMode"/>; observed, not a documented contract).</summary>
    internal const string RetrievalInfoMediaType = RetrievalInfoOutputMode;

    /// <summary>The answer output mode requested under every profile (alias of <see cref="TextPlainOutputMode"/>).</summary>
    internal const string TextPlainMediaType = TextPlainOutputMode;

    private readonly object _discoveryGate = new();
    private Task<RawAgentCard>? _discovery;
    private RawAgentCard? _card;
    private A2AClient? _client;
    private string? _routingTenant;
    private int _captureSequence;
    private string? _nextRpcId;

    /// <summary>Creates the client over an authenticated, captured <see cref="HttpClient"/>.</summary>
    /// <param name="config">The validated connection settings.</param>
    /// <param name="options">The validated runtime options.</param>
    /// <param name="http">The client whose pipeline is <c>WorkIqAuthHandler → RawCaptureHandler → inner</c>.</param>
    /// <param name="capture">The capture handler inside <paramref name="http"/> (its <see cref="RawCaptureHandler.CurrentSink"/> is set per operation).</param>
    /// <param name="clock">The clock used for receipt times, backoff and elapsed measurements.</param>
    /// <param name="diagnostics">The diagnostics every operation traces through; null means <see cref="WorkIqDiagnostics.None"/>.</param>
    public WorkIqA2AClient(
        Microsoft365Config config,
        WorkIqOptions options,
        HttpClient http,
        RawCaptureHandler capture,
        TimeProvider clock,
        WorkIqDiagnostics diagnostics)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Diagnostics = diagnostics ?? WorkIqDiagnostics.None;
        config.Validate();
        options.Validate();
        Policy = new WorkIqTrustPolicy(config.TrustedHosts);
        Profile = options.ProfileOverride ?? config.ResolveProfile();
    }

    /// <summary>The validated connection settings.</summary>
    internal Microsoft365Config Config { get; }

    /// <summary>The validated runtime options.</summary>
    internal WorkIqOptions Options { get; }

    /// <summary>The authenticated, captured HTTP client.</summary>
    internal HttpClient Http { get; }

    /// <summary>The capture handler inside <see cref="Http"/>.</summary>
    internal RawCaptureHandler Capture { get; }

    /// <summary>The clock.</summary>
    internal TimeProvider Clock { get; }

    /// <summary>The trust policy built from <see cref="Microsoft365Config.TrustedHosts"/>.</summary>
    internal WorkIqTrustPolicy Policy { get; }

    /// <summary>The effective diagnostic profile (<see cref="WorkIqOptions.ProfileOverride"/> over the config's).</summary>
    internal DiagnosticProfile Profile { get; }

    /// <summary>The discovered card, or null before discovery completed.</summary>
    internal RawAgentCard? Card => Volatile.Read(ref _card);

    /// <summary>The opaque A2A routing <c>tenant</c> of the selected interface, when the card declared one; null before discovery.</summary>
    internal string? RoutingTenant => Volatile.Read(ref _routingTenant);

    /// <summary>
    /// The SDK client on the selected endpoint, created by discovery (used by the streaming members). Throws
    /// <see cref="InvalidOperationException"/> before <see cref="EnsureDiscoveredAsync"/> completed.
    /// </summary>
    internal A2AClient Client =>
        Volatile.Read(ref _client) ?? throw new InvalidOperationException("The A2A client is created by discovery; await EnsureDiscoveredAsync first.");

    /// <summary>
    /// The JSON-RPC id the next blocking call uses instead of minting one (consumed by that call), so a turn's
    /// identity can carry the id that really went on the wire. Null means mint.
    /// </summary>
    internal string? NextRpcId
    {
        get => Volatile.Read(ref _nextRpcId);
        set => Volatile.Write(ref _nextRpcId, value);
    }

    /// <summary>
    /// The <c>&lt;runId&gt;/&lt;turnId&gt;</c>-style subfolder captures of the current turn are written under
    /// <see cref="WorkIqOptions.RawCaptureDirectory"/> (<see cref="EvidenceContentPolicy.FullEvidence"/> only); null
    /// means <c>&lt;runId&gt;/no-turn</c>. Set by the conversation per turn.
    /// </summary>
    internal string? CaptureScope { get; set; }

    /// <inheritdoc/>
    /// <remarks>Discovery runs once per client and is cached; a failed discovery is retried on the next call.</remarks>
    public Task<RawAgentCard> DiscoverAsync(CancellationToken cancellationToken) => EnsureDiscoveredAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<SendMessageResponse>> SendAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is null)
        {
            throw new ArgumentException("A send needs a message.", nameof(request));
        }

        await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);

        request.Configuration ??= new SendMessageConfiguration();
        request.Configuration.ReturnImmediately = false;
        request.Configuration.AcceptedOutputModes ??= AcceptedOutputModesFor(Profile);
        request.Tenant ??= RoutingTenant;

        return await CallAsync<SendMessageResponse>(
            A2AMethods.SendMessage,
            request,
            WorkIqOperation.Send,
            allowRetry: false,
            taskId: request.Message.TaskId,
            contextId: request.Message.ContextId,
            messageId: request.Message.MessageId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<AgentTask>> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);
        var request = new GetTaskRequest { Id = taskId, HistoryLength = historyLength, Tenant = RoutingTenant };
        return await CallAsync<AgentTask>(A2AMethods.GetTask, request, WorkIqOperation.GetTask, allowRetry: true, taskId, contextId: null, messageId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<AgentTask>> CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);
        var request = new CancelTaskRequest { Id = taskId, Tenant = RoutingTenant };
        return await CallAsync<AgentTask>(A2AMethods.CancelTask, request, WorkIqOperation.CancelTask, allowRetry: true, taskId, contextId: null, messageId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs discovery once (thread-safe), caches the card and creates <see cref="Client"/>; a failed attempt is retried by the next caller.</summary>
    /// <param name="cancellationToken">Cancellation token of the caller that triggers discovery.</param>
    internal Task<RawAgentCard> EnsureDiscoveredAsync(CancellationToken cancellationToken)
    {
        if (Card is { } cached)
        {
            return Task.FromResult(cached);
        }

        Task<RawAgentCard> pending;
        lock (_discoveryGate)
        {
            if (_card is { } card)
            {
                return Task.FromResult(card);
            }

            if (_discovery is null || _discovery.IsFaulted || _discovery.IsCanceled)
            {
                _discovery = DiscoverCoreAsync(cancellationToken);
            }

            pending = _discovery;
        }

        return pending;
    }

    /// <summary>Opens a new capture sink for one operation (from the options) and makes it the capture handler's current sink.</summary>
    /// <param name="op">The operation the capture belongs to (part of the capture id).</param>
    internal RawCaptureSink BeginCapture(WorkIqOperation op)
    {
        var sequence = Interlocked.Increment(ref _captureSequence);
        var captureId = string.Create(CultureInfo.InvariantCulture, $"{op.ToString().ToLowerInvariant()}-{sequence:D4}");
        var directory = Options.RawCaptureDirectory is null
            ? null
            : new DirectoryInfo(Path.Combine(Options.RawCaptureDirectory.FullName, Diagnostics.RunId, CaptureScope ?? "no-turn"));
        var sink = new RawCaptureSink(captureId, Options.ContentPolicy, Options.MaxCaptureBytes, Options.MaxCaptureFrames, directory);
        Capture.CurrentSink = sink;
        return sink;
    }

    /// <summary>Completes a capture opened by <see cref="BeginCapture"/>, detaches it from the handler and returns its reference.</summary>
    /// <param name="sink">The sink.</param>
    /// <param name="interrupted">Whether the operation ended before its reply did (the capture is marked interrupted first).</param>
    internal RawCaptureRef EndCapture(RawCaptureSink sink, bool interrupted = false)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (interrupted)
        {
            sink.MarkInterrupted();
        }

        if (ReferenceEquals(Capture.CurrentSink, sink))
        {
            Capture.CurrentSink = null;
        }

        return sink.Complete();
    }

    /// <summary>Mints a JSON-RPC id (<c>rpc-&lt;32 hex&gt;</c>).</summary>
    internal string NewRpcId() => "rpc-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Classifies an exception raised by a transport operation into a safe <see cref="WorkIqFailure"/> (type, status
    /// and code only — never a body, a token or tenant content). See <see cref="Classify"/>.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <param name="op">The operation that failed.</param>
    /// <param name="requestId">The service <c>request-id</c>, when received.</param>
    /// <param name="taskId">The task id, when known.</param>
    /// <param name="contextId">The context id, when known.</param>
    internal WorkIqFailure ClassifyException(Exception ex, WorkIqOperation op, string? requestId, string? taskId = null, string? contextId = null) =>
        Classify(ex, op, requestId, taskId, contextId, callerToken: default, scope: Config.Scope);

    /// <summary>
    /// The static classifier behind <see cref="ClassifyException"/>: <see cref="WorkIqProtocolException"/> → its own
    /// failure (ids filled in); <see cref="HttpRequestException"/> → 401 Unauthorized, 403 Forbidden, 404 NotFound,
    /// 429 RateLimited, 5xx RemoteError, no status → Transport; <see cref="TaskCanceledException"/> /
    /// <see cref="TimeoutException"/> → Timeout unless <paramref name="callerToken"/> fired (→ Canceled);
    /// <see cref="OperationCanceledException"/> → Canceled; <see cref="JsonException"/> → Malformed;
    /// <see cref="A2AException"/> → by its JSON-RPC code (<c>-32009</c> ProtocolVersionMismatch, <c>-32001</c> NotFound,
    /// otherwise RemoteError with the code); anything else → Unknown. Certainty is Indeterminate for a transport
    /// failure, timeout or cancellation (the request may have been received) and for a 5xx on a send.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <param name="op">The operation that failed.</param>
    /// <param name="requestId">The service <c>request-id</c>, when received.</param>
    /// <param name="taskId">The task id, when known.</param>
    /// <param name="contextId">The context id, when known.</param>
    /// <param name="callerToken">The caller's token, to tell a local cancellation from a timeout.</param>
    /// <param name="scope">The configured scope, named in the 401 hint.</param>
    internal static WorkIqFailure Classify(
        Exception ex,
        WorkIqOperation op,
        string? requestId,
        string? taskId = null,
        string? contextId = null,
        CancellationToken callerToken = default,
        string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        switch (ex)
        {
            case WorkIqProtocolException protocol:
                return protocol.Failure with
                {
                    RequestId = protocol.Failure.RequestId ?? requestId,
                    TaskId = protocol.Failure.TaskId ?? taskId,
                    ContextId = protocol.Failure.ContextId ?? contextId,
                };

            case A2AException a2a when a2a.InnerException is HttpRequestException innerHttp:
                return Classify(innerHttp, op, requestId, taskId, contextId, callerToken, scope);

            case A2AException a2a:
                return ClassifyRpcError((int)a2a.ErrorCode, op, requestId, rpcRequestId: null, taskId, contextId, capture: null);

            case HttpRequestException http when http.StatusCode is { } status:
                return ClassifyHttpStatus((int)status, op, requestId, rpcRequestId: null, taskId, contextId, capture: null, scope);

            case HttpRequestException http:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Transport,
                    $"{op}: {http.GetType().Name} without an HTTP status (connection, DNS, TLS or socket failure)",
                    OutcomeCertainty.Indeterminate,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            case OperationCanceledException canceled when callerToken.IsCancellationRequested
                || (canceled is not TaskCanceledException && canceled.CancellationToken.IsCancellationRequested):
                return new WorkIqFailure(
                    WorkIqFailureCategory.Canceled,
                    $"{op}: the local wait was canceled by the caller's token; the remote state was not reconciled",
                    OutcomeCertainty.Indeterminate,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            case TaskCanceledException timeout:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Timeout,
                    $"{op}: the HTTP call timed out locally ({timeout.InnerException?.GetType().Name ?? "no inner exception"}); the request may have reached the service",
                    OutcomeCertainty.Indeterminate,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            case OperationCanceledException:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Canceled,
                    $"{op}: the local wait was canceled; the remote state was not reconciled",
                    OutcomeCertainty.Indeterminate,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            case TimeoutException:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Timeout,
                    $"{op}: timed out locally; the request may have reached the service",
                    OutcomeCertainty.Indeterminate,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            case JsonException json:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Malformed,
                    $"{op}: the reply could not be parsed as A2A 1.0 ({json.GetType().Name}{(json.Path is null ? "" : " at " + json.Path)})",
                    OutcomeCertainty.Known,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);

            default:
                return new WorkIqFailure(
                    WorkIqFailureCategory.Unknown,
                    $"{op}: {ex.GetType().FullName ?? ex.GetType().Name}",
                    op is WorkIqOperation.Send or WorkIqOperation.Stream ? OutcomeCertainty.Indeterminate : OutcomeCertainty.Known,
                    op,
                    RequestId: requestId,
                    TaskId: taskId,
                    ContextId: contextId);
        }
    }

    /// <summary>
    /// Classifies a non-success HTTP status into a safe failure. The message carries the operation, the numeric status
    /// (<c>HTTP 429</c> — never the status enum name, never the body), a hint, the <c>Retry-After</c> wait as
    /// <c>retry-after=&lt;seconds&gt;</c> when the reply carried one (the form <see cref="WorkIqIssueClassifier"/> scans
    /// for) and the service <c>request-id</c> when received.
    /// </summary>
    internal static WorkIqFailure ClassifyHttpStatus(
        int status,
        WorkIqOperation op,
        string? requestId,
        string? rpcRequestId,
        string? taskId,
        string? contextId,
        RawCaptureRef? capture,
        string? scope,
        TimeSpan? retryAfter = null)
    {
        var (category, hint) = status switch
        {
            401 => (WorkIqFailureCategory.Unauthorized, $"token rejected: audience/scope/expiry — re-run the device-code sign-in; scope must be {scope ?? Microsoft365Config.DefaultScope}"),
            403 => (WorkIqFailureCategory.Forbidden, $"the signed-in user or the app lacks the delegated permission, its admin consent or a Work IQ licence (scope {scope ?? Microsoft365Config.DefaultScope})"),
            404 => (WorkIqFailureCategory.NotFound, "endpoint, agent or task not found (a task may be inaccessible, expired or purged)"),
            429 => (WorkIqFailureCategory.RateLimited, "rate limited; backoff applies to discovery/GetTask/CancelTask only, a send is never resent"),
            >= 500 => (WorkIqFailureCategory.RemoteError, "service error"),
            _ => (WorkIqFailureCategory.Transport, "unexpected HTTP status"),
        };
        var certainty = status >= 500 && op is WorkIqOperation.Send or WorkIqOperation.Stream
            ? OutcomeCertainty.Indeterminate
            : OutcomeCertainty.Known;
        var retryAfterPart = retryAfter is { } wait
            ? $"; retry-after={Math.Max(0L, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}"
            : "";
        var requestIdPart = string.IsNullOrWhiteSpace(requestId) ? "" : $"; request-id {requestId}";
        return new WorkIqFailure(
            category,
            $"{op}: HTTP {status}: {hint}{retryAfterPart}{requestIdPart}",
            certainty,
            op,
            HttpStatus: status,
            RequestId: requestId,
            RpcRequestId: rpcRequestId,
            TaskId: taskId,
            ContextId: contextId,
            Capture: capture);
    }

    /// <summary>Classifies a JSON-RPC error code into a safe failure (<c>-32009</c> ProtocolVersionMismatch, <c>-32001</c> NotFound, else RemoteError with the code).</summary>
    internal static WorkIqFailure ClassifyRpcError(
        int code,
        WorkIqOperation op,
        string? requestId,
        string? rpcRequestId,
        string? taskId,
        string? contextId,
        RawCaptureRef? capture)
    {
        var (category, description) = code switch
        {
            (int)A2AErrorCode.VersionNotSupported => (WorkIqFailureCategory.ProtocolVersionMismatch, "VersionNotSupported: the service rejected A2A 1.0; this provider never downgrades to 0.3"),
            (int)A2AErrorCode.TaskNotFound => (WorkIqFailureCategory.NotFound, "TaskNotFound: the task is inaccessible, expired or purged (not proof it never existed)"),
            (int)A2AErrorCode.TaskNotCancelable => (WorkIqFailureCategory.RemoteError, "TaskNotCancelable: the task is already terminal"),
            (int)A2AErrorCode.MethodNotFound => (WorkIqFailureCategory.RemoteError, "MethodNotFound: the service does not implement this JSON-RPC method"),
            (int)A2AErrorCode.ContentTypeNotSupported => (WorkIqFailureCategory.RemoteError, "ContentTypeNotSupported: an accepted output mode was refused (the request was not retried in answer-only mode)"),
            (int)A2AErrorCode.InvalidParams => (WorkIqFailureCategory.RemoteError, "InvalidParams"),
            (int)A2AErrorCode.InvalidRequest => (WorkIqFailureCategory.RemoteError, "InvalidRequest"),
            (int)A2AErrorCode.ParseError => (WorkIqFailureCategory.RemoteError, "ParseError: the service could not parse the envelope"),
            (int)A2AErrorCode.InternalError => (WorkIqFailureCategory.RemoteError, "InternalError"),
            (int)A2AErrorCode.UnsupportedOperation => (WorkIqFailureCategory.RemoteError, "UnsupportedOperation"),
            (int)A2AErrorCode.PushNotificationNotSupported => (WorkIqFailureCategory.RemoteError, "PushNotificationNotSupported"),
            (int)A2AErrorCode.InvalidAgentResponse => (WorkIqFailureCategory.RemoteError, "InvalidAgentResponse"),
            (int)A2AErrorCode.ExtendedAgentCardNotConfigured => (WorkIqFailureCategory.RemoteError, "ExtendedAgentCardNotConfigured"),
            (int)A2AErrorCode.ExtensionSupportRequired => (WorkIqFailureCategory.RemoteError, "ExtensionSupportRequired"),
            _ => (WorkIqFailureCategory.RemoteError, "unlisted JSON-RPC error code"),
        };
        return new WorkIqFailure(
            category,
            $"{op}: JSON-RPC error {code} ({description})",
            OutcomeCertainty.Known,
            op,
            RpcErrorCode: code,
            RequestId: requestId,
            RpcRequestId: rpcRequestId,
            TaskId: taskId,
            ContextId: contextId,
            Capture: capture);
    }

    /// <summary>The issue category a failure category maps to (<see cref="WorkIqFailureCategory.Canceled"/> raises no issue).</summary>
    /// <param name="category">The failure category.</param>
    internal static WorkIqIssueCategory? IssueCategoryOf(WorkIqFailureCategory category) => category switch
    {
        WorkIqFailureCategory.Unauthorized => WorkIqIssueCategory.Authentication,
        WorkIqFailureCategory.Forbidden => WorkIqIssueCategory.Permission,
        WorkIqFailureCategory.Transport => WorkIqIssueCategory.Network,
        WorkIqFailureCategory.Timeout => WorkIqIssueCategory.Network,
        WorkIqFailureCategory.RateLimited => WorkIqIssueCategory.Service,
        WorkIqFailureCategory.RemoteError => WorkIqIssueCategory.Service,
        WorkIqFailureCategory.NotFound => WorkIqIssueCategory.Service,
        WorkIqFailureCategory.ProtocolVersionMismatch => WorkIqIssueCategory.Protocol,
        WorkIqFailureCategory.Malformed => WorkIqIssueCategory.Protocol,
        WorkIqFailureCategory.UntrustedHost => WorkIqIssueCategory.Protocol,
        WorkIqFailureCategory.Canceled => null,
        _ => WorkIqIssueCategory.Unknown,
    };

    /// <summary>A suggested cause for a failure category, for the issue register.</summary>
    /// <param name="category">The failure category.</param>
    internal static string SuggestedCauseOf(WorkIqFailureCategory category) => category switch
    {
        WorkIqFailureCategory.Unauthorized => "The bearer token was rejected: wrong audience/scope, expired, or the sign-in was for another tenant. Re-run the device-code sign-in with the configured scope.",
        WorkIqFailureCategory.Forbidden => "The signed-in user or the app registration lacks the delegated WorkIQAgent.Ask permission, its admin consent, or a Work IQ licence.",
        WorkIqFailureCategory.Transport => "Network, DNS, proxy or TLS problem between this machine and the Work IQ endpoint.",
        WorkIqFailureCategory.Timeout => "The service did not answer within the configured timeout; raise SendTimeout/RecoveryBudget or check connectivity.",
        WorkIqFailureCategory.RateLimited => "The tenant or app is throttled; wait for Retry-After and reduce concurrency.",
        WorkIqFailureCategory.RemoteError => "The service reported an error or ended the task failed/rejected; see the raw capture and the request-id when contacting support.",
        WorkIqFailureCategory.NotFound => "The agent id, endpoint or task does not exist for this principal (a task may have expired or been purged).",
        WorkIqFailureCategory.ProtocolVersionMismatch => "The endpoint speaks A2A 0.3 or rejected 1.0; this provider never downgrades. Check the A2A-Version header and the card's supportedInterfaces.",
        WorkIqFailureCategory.Malformed => "The reply is not a valid A2A 1.0 envelope; the raw capture shows what arrived.",
        WorkIqFailureCategory.UntrustedHost => "The card or an interface URL points at a host outside trustedHosts; the token was not sent. Fix the config or the card.",
        WorkIqFailureCategory.Canceled => "The local wait was canceled; the remote task state is unknown.",
        _ => "Unclassified; see the trace and the raw capture.",
    };

    /// <summary>Registers an issue for a failure (none for a local cancellation) and traces <c>rpc.error</c>.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="rpcId">The JSON-RPC id, when any.</param>
    /// <param name="elapsedMs">Elapsed milliseconds, when measured.</param>
    internal void ReportFailure(WorkIqFailure failure, WorkIqTraceStage stage, string? rpcId, double? elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Error, "rpc.error", $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}")
        {
            RpcId = rpcId,
            TaskId = failure.TaskId,
            ContextId = failure.ContextId,
            RequestId = failure.RequestId,
            HttpStatus = failure.HttpStatus,
            RpcErrorCode = failure.RpcErrorCode,
            A2AState = failure.RemoteTaskState,
            ElapsedMs = elapsedMs,
        });

        if (IssueCategoryOf(failure.Category) is { } category)
        {
            Diagnostics.Issue(new WorkIqIssue(
                default,
                stage,
                category,
                $"{failure.Operation.ToString().ToLowerInvariant()}.{failure.Category.ToString().ToLowerInvariant()}",
                failure.SafeMessage,
                SuggestedCauseOf(failure.Category))
            {
                HttpStatus = failure.HttpStatus,
                RpcErrorCode = failure.RpcErrorCode,
                A2AState = failure.RemoteTaskState,
                RequestId = failure.RequestId,
                TaskId = failure.TaskId,
                ContextId = failure.ContextId,
            });
        }
    }

    /// <summary>The trace stage of an operation.</summary>
    /// <param name="op">The operation.</param>
    internal static WorkIqTraceStage StageOf(WorkIqOperation op) => op switch
    {
        WorkIqOperation.Discover => WorkIqTraceStage.Discovery,
        WorkIqOperation.Send => WorkIqTraceStage.Send,
        WorkIqOperation.Stream => WorkIqTraceStage.Stream,
        WorkIqOperation.GetTask => WorkIqTraceStage.GetTask,
        WorkIqOperation.CancelTask => WorkIqTraceStage.Cancel,
        WorkIqOperation.Subscribe => WorkIqTraceStage.Subscribe,
        _ => WorkIqTraceStage.Send,
    };

    /// <summary>
    /// Detects an A2A 0.3-shaped result (a <c>kind</c> discriminator on the result, its <c>task</c>/<c>message</c>,
    /// or a <c>status.state</c> that is not a <c>TASK_STATE_*</c> value); returns a safe description or null.
    /// </summary>
    /// <param name="result">The JSON-RPC <c>result</c> node.</param>
    internal static string? DetectLegacyShape(JsonNode? result)
    {
        if (result is not JsonObject obj)
        {
            return null;
        }

        if (obj.ContainsKey("kind"))
        {
            return "the result carries a 'kind' discriminator (A2A 0.3 shape)";
        }

        foreach (var key in new[] { "task", "message" })
        {
            if (obj[key] is JsonObject inner && inner.ContainsKey("kind"))
            {
                return $"result.{key} carries a 'kind' discriminator (A2A 0.3 shape)";
            }
        }

        var task = obj["task"] as JsonObject ?? (obj.ContainsKey("status") ? obj : null);
        if (task?["status"] is JsonObject status
            && status["state"] is JsonValue stateValue
            && stateValue.TryGetValue<string>(out var state)
            && !state.StartsWith("TASK_STATE_", StringComparison.Ordinal))
        {
            return $"status.state '{state}' is not an A2A 1.0 TASK_STATE_* value (0.3 shape)";
        }

        return null;
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The bytes.</param>
    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The wait before retry number <paramref name="attempt"/> (0-based): <c>Retry-After</c> when present (capped), else exponential with jitter.</summary>
    /// <param name="attempt">The 0-based retry index.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> value, when the reply carried one.</param>
    internal static TimeSpan ComputeRetryDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } header)
        {
            if (header < TimeSpan.Zero)
            {
                header = TimeSpan.Zero;
            }

            return header > RetryMaxDelay ? RetryMaxDelay : header;
        }

        var exponential = TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * RetryBaseDelay.TotalMilliseconds);
        var delay = exponential + jitter;
        return delay > RetryMaxDelay ? RetryMaxDelay : delay;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------------------------------------

    private async Task<RawAgentCard> DiscoverCoreAsync(CancellationToken cancellationToken)
    {
        var cardUri = Config.ResolveCardUri();
        var started = Clock.GetTimestamp();
        Diagnostics.Trace(WorkIqTraceStage.Discovery, "discovery.card_requested", $"GET agent card {cardUri.Scheme}://{cardUri.Host}{cardUri.AbsolutePath}");

        if (!Policy.IsTrusted(cardUri))
        {
            var refused = new WorkIqFailure(
                WorkIqFailureCategory.UntrustedHost,
                $"Discover: the card URI {cardUri.Scheme}://{cardUri.Host} is outside the trust policy (trusted: {string.Join(", ", Policy.TrustedHosts)}); nothing was sent",
                OutcomeCertainty.Known,
                WorkIqOperation.Discover);
            ReportDiscoveryFailure(refused, "discovery.untrusted_host", Clock.GetElapsedTime(started).TotalMilliseconds);
            throw new WorkIqProtocolException(refused);
        }

        var sink = BeginCapture(WorkIqOperation.Discover);
        HttpExchange exchange;
        try
        {
            exchange = await ExchangeWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, cardUri), WorkIqOperation.Discover, allowRetry: true, taskId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not WorkIqProtocolException)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = Classify(ex, WorkIqOperation.Discover, requestId: null, callerToken: cancellationToken, scope: Config.Scope) with { Capture = capture };
            ReportDiscoveryFailure(failure, "discovery.failed", Clock.GetElapsedTime(started).TotalMilliseconds);
            throw new WorkIqProtocolException(failure, ex);
        }
        catch (WorkIqProtocolException ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = ex.Failure with { Capture = ex.Failure.Capture ?? capture };
            ReportDiscoveryFailure(failure, "discovery.failed", Clock.GetElapsedTime(started).TotalMilliseconds);
            throw new WorkIqProtocolException(failure, ex);
        }

        var captureRef = EndCapture(sink);
        var elapsedMs = Clock.GetElapsedTime(started).TotalMilliseconds;
        if (!exchange.Success)
        {
            var failure = ClassifyHttpStatus(exchange.HttpStatus, WorkIqOperation.Discover, exchange.RequestId, null, null, null, captureRef, Config.Scope, exchange.RetryAfter);
            ReportDiscoveryFailure(failure, "discovery.failed", elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        AgentCard? card;
        try
        {
            card = JsonSerializer.Deserialize<AgentCard>(exchange.Body, A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, WorkIqOperation.Discover, exchange.RequestId) with { Capture = captureRef, HttpStatus = exchange.HttpStatus };
            ReportDiscoveryFailure(failure, "discovery.failed", elapsedMs);
            throw new WorkIqProtocolException(failure, ex);
        }

        if (card is null)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                "Discover: the agent card body deserialized to null",
                OutcomeCertainty.Known,
                WorkIqOperation.Discover,
                HttpStatus: exchange.HttpStatus,
                RequestId: exchange.RequestId,
                Capture: captureRef);
            ReportDiscoveryFailure(failure, "discovery.failed", elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        var sha256 = Sha256Hex(exchange.Bytes);
        Diagnostics.Trace(
            WorkIqTraceStage.Discovery,
            "discovery.card_fetched",
            $"agent card fetched: {exchange.Bytes.Length} bytes, sha256 {sha256[..12]}…, {card.SupportedInterfaces?.Count ?? 0} interface(s), " +
            $"{card.Skills?.Count ?? 0} skill(s), streaming={Describe(card.Capabilities?.Streaming)}, pushNotifications={Describe(card.Capabilities?.PushNotifications)}",
            requestId: exchange.RequestId,
            httpStatus: exchange.HttpStatus,
            elapsedMs: elapsedMs);

        AgentInterface selected;
        try
        {
            selected = Policy.SelectInterface(card, cardUri);
        }
        catch (WorkIqProtocolException ex)
        {
            var failure = ex.Failure with { Capture = captureRef, HttpStatus = exchange.HttpStatus, RequestId = exchange.RequestId };
            ReportDiscoveryFailure(
                failure,
                failure.Category == WorkIqFailureCategory.UntrustedHost ? "discovery.untrusted_host" : "discovery.version_mismatch",
                elapsedMs);
            throw new WorkIqProtocolException(failure, ex);
        }

        var endpoint = new Uri(selected.Url, UriKind.Absolute);
        var raw = new RawAgentCard(card, exchange.Body, sha256, cardUri, endpoint, selected.ProtocolVersion.Trim(), exchange.ReceivedAt);
        Volatile.Write(ref _routingTenant, string.IsNullOrWhiteSpace(selected.Tenant) ? null : selected.Tenant);
        Volatile.Write(ref _client, new A2AClient(endpoint, Http));
        Volatile.Write(ref _card, raw);
        Diagnostics.Trace(
            WorkIqTraceStage.Discovery,
            "discovery.interface_selected",
            $"selected {selected.ProtocolBinding} {raw.SelectedProtocolVersion} at {endpoint.Scheme}://{endpoint.Host}{endpoint.AbsolutePath} (routing tenant {(RoutingTenant is null ? "absent" : "present")})",
            requestId: exchange.RequestId,
            elapsedMs: Clock.GetElapsedTime(started).TotalMilliseconds);
        return raw;
    }

    private void ReportDiscoveryFailure(WorkIqFailure failure, string code, double elapsedMs)
    {
        Diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Discovery, WorkIqTraceLevel.Error, code, $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}")
        {
            RequestId = failure.RequestId,
            HttpStatus = failure.HttpStatus,
            RpcErrorCode = failure.RpcErrorCode,
            ElapsedMs = elapsedMs,
        });

        if (IssueCategoryOf(failure.Category) is { } category)
        {
            Diagnostics.Issue(new WorkIqIssue(default, WorkIqTraceStage.Discovery, category, code, failure.SafeMessage, SuggestedCauseOf(failure.Category))
            {
                HttpStatus = failure.HttpStatus,
                RpcErrorCode = failure.RpcErrorCode,
                RequestId = failure.RequestId,
            });
        }
    }

    private static string Describe(bool? value) => value is null ? "unknown" : value.Value ? "true" : "false";

    // ---------------------------------------------------------------------------------------------------------
    // JSON-RPC calls
    // ---------------------------------------------------------------------------------------------------------

    private async Task<WorkIqRpcResult<T>> CallAsync<T>(
        string method,
        object parameters,
        WorkIqOperation op,
        bool allowRetry,
        string? taskId,
        string? contextId,
        string? messageId,
        CancellationToken cancellationToken)
        where T : class
    {
        var card = Card ?? await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);
        var stage = StageOf(op);
        var rpcId = Interlocked.Exchange(ref _nextRpcId, null) ?? NewRpcId();
        var envelope = new JsonRpcRequest
        {
            Method = method,
            Id = new JsonRpcId(rpcId),
            Params = JsonSerializer.SerializeToElement(parameters, parameters.GetType(), A2AJsonUtilities.DefaultOptions),
        };

        var started = Clock.GetTimestamp();
        Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Info, "rpc.sent", $"{method} → {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath}{(allowRetry ? "" : " (never retried)")}")
        {
            RpcId = rpcId,
            MessageId = messageId,
            TaskId = taskId,
            ContextId = contextId,
        });

        var sink = BeginCapture(op);
        HttpExchange exchange;
        try
        {
            exchange = await ExchangeWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Post, card.SelectedEndpoint) { Content = new JsonRpcContent(envelope) },
                op,
                allowRetry,
                taskId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkIqProtocolException ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = ex.Failure with
            {
                Capture = ex.Failure.Capture ?? capture,
                RpcRequestId = ex.Failure.RpcRequestId ?? rpcId,
                TaskId = ex.Failure.TaskId ?? taskId,
                ContextId = ex.Failure.ContextId ?? contextId,
            };
            ReportFailure(failure, stage, rpcId, Clock.GetElapsedTime(started).TotalMilliseconds);
            throw new WorkIqProtocolException(failure, ex);
        }
        catch (Exception ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = Classify(ex, op, requestId: null, taskId, contextId, cancellationToken, Config.Scope) with { Capture = capture, RpcRequestId = rpcId };
            ReportFailure(failure, stage, rpcId, Clock.GetElapsedTime(started).TotalMilliseconds);
            throw new WorkIqProtocolException(failure, ex);
        }

        var captureRef = EndCapture(sink);
        var elapsedMs = Clock.GetElapsedTime(started).TotalMilliseconds;

        if (!exchange.Success)
        {
            var failure = ClassifyHttpStatus(exchange.HttpStatus, op, exchange.RequestId, rpcId, taskId, contextId, captureRef, Config.Scope, exchange.RetryAfter);
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        JsonRpcResponse? rpc;
        try
        {
            rpc = JsonSerializer.Deserialize<JsonRpcResponse>(exchange.Body, A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, op, exchange.RequestId, taskId, contextId) with { Capture = captureRef, RpcRequestId = rpcId, HttpStatus = exchange.HttpStatus };
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure, ex);
        }

        if (rpc is null)
        {
            var failure = Malformed(op, "the JSON-RPC envelope deserialized to null", exchange, rpcId, taskId, contextId, captureRef);
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        if (rpc.Error is { } error)
        {
            var failure = ClassifyRpcError(error.Code, op, exchange.RequestId, rpcId, taskId, contextId, captureRef) with { HttpStatus = exchange.HttpStatus };
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        if (rpc.Id.HasValue && !string.Equals(rpc.Id.ToString(), rpcId, StringComparison.Ordinal))
        {
            Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Warning, "rpc.id_mismatch", $"{method}: the reply's JSON-RPC id differs from the request id (reply correlated by HTTP exchange)")
            {
                RpcId = rpcId,
                RequestId = exchange.RequestId,
                HttpStatus = exchange.HttpStatus,
                TaskId = taskId,
                ContextId = contextId,
            });
        }

        if (rpc.Result is null)
        {
            var failure = Malformed(op, "the JSON-RPC envelope carries neither result nor error", exchange, rpcId, taskId, contextId, captureRef);
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        if (DetectLegacyShape(rpc.Result) is { } legacy)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.ProtocolVersionMismatch,
                $"{op}: {legacy}; this provider speaks 1.0 only and never downgrades",
                OutcomeCertainty.Known,
                op,
                HttpStatus: exchange.HttpStatus,
                RequestId: exchange.RequestId,
                RpcRequestId: rpcId,
                TaskId: taskId,
                ContextId: contextId,
                Capture: captureRef);
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        T? value;
        try
        {
            value = rpc.Result.Deserialize<T>(A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, op, exchange.RequestId, taskId, contextId) with { Capture = captureRef, RpcRequestId = rpcId, HttpStatus = exchange.HttpStatus };
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure, ex);
        }

        if (value is null)
        {
            var failure = Malformed(op, "the JSON-RPC result deserialized to null", exchange, rpcId, taskId, contextId, captureRef);
            ReportFailure(failure, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(failure);
        }

        if (ValidateResult(value, op, taskId, exchange, rpcId, contextId, captureRef) is { } invalid)
        {
            ReportFailure(invalid, stage, rpcId, elapsedMs);
            throw new WorkIqProtocolException(invalid);
        }

        var (replyTaskId, replyContextId, replyState, replyMessageId) = DescribeResult(value);
        Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Info, "rpc.response", $"{method} ← HTTP {exchange.HttpStatus}: {DescribeShape(value)}")
        {
            RpcId = rpcId,
            MessageId = replyMessageId,
            TaskId = replyTaskId ?? taskId,
            ContextId = replyContextId ?? contextId,
            RequestId = exchange.RequestId,
            HttpStatus = exchange.HttpStatus,
            A2AState = replyState,
            ElapsedMs = elapsedMs,
        });

        return new WorkIqRpcResult<T>(value, captureRef, exchange.RequestId, exchange.HttpStatus, exchange.ReceivedAt);
    }

    private static WorkIqFailure Malformed(WorkIqOperation op, string what, HttpExchange exchange, string rpcId, string? taskId, string? contextId, RawCaptureRef capture) =>
        new(
            WorkIqFailureCategory.Malformed,
            $"{op}: {what}",
            OutcomeCertainty.Known,
            op,
            HttpStatus: exchange.HttpStatus,
            RequestId: exchange.RequestId,
            RpcRequestId: rpcId,
            TaskId: taskId,
            ContextId: contextId,
            Capture: capture);

    private WorkIqFailure? ValidateResult(object value, WorkIqOperation op, string? requestedTaskId, HttpExchange exchange, string rpcId, string? contextId, RawCaptureRef capture)
    {
        switch (value)
        {
            case SendMessageResponse reply when reply.Task is null && reply.Message is null:
                return Malformed(op, "SendMessage reply carries neither task nor message (one-of violation)", exchange, rpcId, null, contextId, capture);
            case SendMessageResponse reply when reply.Task is not null && reply.Message is not null:
                return Malformed(op, "SendMessage reply carries both task and message (one-of violation)", exchange, rpcId, reply.Task.Id, contextId, capture);
            case AgentTask task when requestedTaskId is not null && !string.Equals(task.Id, requestedTaskId, StringComparison.Ordinal):
                Diagnostics.Trace(new WorkIqTraceEvent(default, StageOf(op), WorkIqTraceLevel.Warning, "rpc.task_id_mismatch", $"{op}: the reply describes another task than the one requested")
                {
                    RpcId = rpcId,
                    TaskId = requestedTaskId,
                    RequestId = exchange.RequestId,
                    HttpStatus = exchange.HttpStatus,
                    A2AState = RawState(task.Status.State),
                });
                return null;
            default:
                return null;
        }
    }

    private static (string? TaskId, string? ContextId, string? State, string? MessageId) DescribeResult(object value) => value switch
    {
        AgentTask task => (task.Id, task.ContextId, RawState(task.Status?.State), task.Status?.Message?.MessageId),
        SendMessageResponse { Task: { } task } => (task.Id, task.ContextId, RawState(task.Status?.State), task.Status?.Message?.MessageId),
        SendMessageResponse { Message: { } message } => (message.TaskId, message.ContextId, null, message.MessageId),
        _ => (null, null, null, null),
    };

    private static string DescribeShape(object value) => value switch
    {
        AgentTask task => $"task in state {RawState(task.Status?.State) ?? "(absent)"} with {task.Artifacts?.Count ?? 0} artifact(s)",
        SendMessageResponse { Task: { } task } => $"task in state {RawState(task.Status?.State) ?? "(absent)"} with {task.Artifacts?.Count ?? 0} artifact(s)",
        SendMessageResponse { Message: { } message } => $"direct message with {message.Parts?.Count ?? 0} part(s)",
        _ => value.GetType().Name,
    };

    /// <summary>The wire form of a task state (<c>TASK_STATE_*</c>), or null when absent.</summary>
    /// <param name="state">The SDK state.</param>
    internal static string? RawState(TaskState? state)
    {
        if (state is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(state.Value, A2AJsonUtilities.DefaultOptions).Trim('"');
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return state.Value.ToString();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // HTTP exchange + bounded retry
    // ---------------------------------------------------------------------------------------------------------

    private sealed record HttpExchange(int HttpStatus, bool Success, string? RequestId, byte[] Bytes, string Body, DateTimeOffset ReceivedAt, TimeSpan? RetryAfter);

    private async Task<HttpExchange> ExchangeWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        WorkIqOperation op,
        bool allowRetry,
        string? taskId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpExchange exchange;
            using (var request = requestFactory())
            {
                exchange = await ExchangeOnceAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (exchange.Success || !allowRetry || attempt >= MaxRetries || exchange.HttpStatus is not (429 or 503))
            {
                return exchange;
            }

            var delay = ComputeRetryDelay(attempt, exchange.RetryAfter);
            Diagnostics.Trace(
                StageOf(op),
                "rpc.retry",
                $"{op}: HTTP {exchange.HttpStatus}; retry {attempt + 1}/{MaxRetries} after {delay.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms " +
                $"(Retry-After {(exchange.RetryAfter is null ? "absent" : "honoured")})",
                WorkIqTraceLevel.Warning,
                taskId: taskId,
                requestId: exchange.RequestId,
                httpStatus: exchange.HttpStatus);
            await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpExchange> ExchangeOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var body = Encoding.UTF8.GetString(bytes);
        var requestId = ReadRequestId(request, response);
        return new HttpExchange((int)response.StatusCode, response.IsSuccessStatusCode, requestId, bytes, body, Clock.GetUtcNow(), ReadRetryAfter(response));
    }

    private static string? ReadRequestId(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(WorkIqAuthHandler.RequestIdHeader, out var values))
        {
            var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (first is not null)
            {
                return first.Trim();
            }
        }

        return request.Options.TryGetValue(WorkIqAuthHandler.RequestIdOption, out var captured) && !string.IsNullOrWhiteSpace(captured)
            ? captured
            : null;
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - Clock.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
