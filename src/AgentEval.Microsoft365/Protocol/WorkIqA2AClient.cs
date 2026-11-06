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
/// whose pipeline is <c>WorkIqAuthHandler → RawCaptureHandler → inner</c>. The blocking operations live here; the two
/// streaming members live in <c>WorkIqA2AClient.Streaming.cs</c>.
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
/// <para>
/// Every failure is traced here. Its issue is registered here once, classified by <see cref="WorkIqIssueClassifier"/>,
/// only when this client is the layer that ends the operation (<see cref="RegistersIssue"/>): a turn registers the
/// issue of its own outcome, the auth layer the issue of a token failure. The per-call facts (JSON-RPC id, capture
/// folder, the capture of a stream) travel on the <see cref="WorkIqCallContext"/>, and a blocking call's capture sink
/// travels on its own request, so two calls on one client never overwrite each other's.
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

    /// <summary>The trace code of a failure the caller declared expected (<see cref="ExpectFailure"/>), traced at Info.</summary>
    internal const string ExpectedErrorCode = "rpc.expected_error";

    private readonly object _discoveryGate = new();
    private Func<WorkIqFailure, bool>? _expectedFailure;
    private Task<RawAgentCard>? _discovery;
    private RawAgentCard? _card;
    private A2AClient? _client;
    private int _captureSequence;

    /// <summary>Creates the client over an authenticated, captured <see cref="HttpClient"/>.</summary>
    /// <param name="config">The validated connection settings.</param>
    /// <param name="options">The validated runtime options.</param>
    /// <param name="http">The client whose pipeline is <c>WorkIqAuthHandler → RawCaptureHandler → inner</c>.</param>
    /// <param name="capture">The capture handler inside <paramref name="http"/> (its <see cref="RawCaptureHandler.CurrentSink"/> is set while a stream is open).</param>
    /// <param name="clock">The clock used for receipt times, backoff and elapsed measurements.</param>
    /// <param name="diagnostics">The diagnostics every operation traces through; null means <see cref="WorkIqDiagnostics.None"/>.</param>
    /// <param name="policy">The trust policy the transport already enforces (the factory passes the auth handler's); null builds one from <see cref="Microsoft365Config.TrustedHosts"/>.</param>
    public WorkIqA2AClient(
        Microsoft365Config config,
        WorkIqOptions options,
        HttpClient http,
        RawCaptureHandler capture,
        TimeProvider clock,
        WorkIqDiagnostics diagnostics,
        WorkIqTrustPolicy? policy = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Diagnostics = diagnostics ?? WorkIqDiagnostics.None;
        config.Validate();
        options.Validate();
        Policy = policy ?? new WorkIqTrustPolicy(config.TrustedHosts);
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

    /// <summary>
    /// The diagnostics every operation of this client traces through (<c>discovery.*</c>, <c>rpc.sent</c>,
    /// <c>rpc.response</c>, <c>rpc.error</c>, <c>task.state</c>, the stream events); never null.
    /// </summary>
    internal WorkIqDiagnostics Diagnostics { get; }

    /// <summary>
    /// The auth handler inside <see cref="Http"/>, when the client should acquire the bearer token itself before every
    /// HTTP call (the live transport sets it): the token wait — a device-code sign-in included — then runs under the
    /// operation's own token, outside <c>HttpClient.Timeout</c>, so a slow sign-in ends as "sign-in not completed,
    /// nothing was sent" and never as a network timeout. Null leaves the acquisition inside the pipeline.
    /// </summary>
    internal WorkIqAuthHandler? Auth { get; init; }

    /// <summary>The clock.</summary>
    internal TimeProvider Clock { get; }

    /// <summary>The trust policy built from <see cref="Microsoft365Config.TrustedHosts"/>.</summary>
    internal WorkIqTrustPolicy Policy { get; }

    /// <summary>The effective diagnostic profile (<see cref="WorkIqOptions.ProfileOverride"/> over the config's).</summary>
    internal DiagnosticProfile Profile { get; }

    /// <summary>The discovered card, or null before discovery completed.</summary>
    internal RawAgentCard? Card => Volatile.Read(ref _card);

    /// <summary>The opaque A2A routing <c>tenant</c> of the selected interface (<see cref="RawAgentCard.RoutingTenant"/>); null before discovery or when the card declares none.</summary>
    internal string? RoutingTenant => Card?.RoutingTenant;

    /// <summary>
    /// The SDK client on the selected endpoint, created by discovery (used by the streaming members). Throws
    /// <see cref="InvalidOperationException"/> before <see cref="EnsureDiscoveredAsync"/> completed.
    /// </summary>
    internal A2AClient Client =>
        Volatile.Read(ref _client) ?? throw new InvalidOperationException("The A2A client is created by discovery; await EnsureDiscoveredAsync first.");

    /// <inheritdoc/>
    /// <remarks>Discovery runs once per client and is cached; a failed discovery is retried on the next call.</remarks>
    public Task<RawAgentCard> DiscoverAsync(CancellationToken cancellationToken, WorkIqCallContext? call = null) => EnsureDiscoveredAsync(cancellationToken, call);

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<SendMessageResponse>> SendAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is null)
        {
            throw new ArgumentException("A send needs a message.", nameof(request));
        }

        await EnsureDiscoveredAsync(cancellationToken, call).ConfigureAwait(false);
        ApplySendConfiguration(request, Profile, Options.HistoryLength, RoutingTenant);

        return await CallAsync<SendMessageResponse>(
            A2AMethods.SendMessage,
            request,
            WorkIqOperation.Send,
            allowRetry: false,
            taskId: request.Message.TaskId,
            contextId: request.Message.ContextId,
            messageId: request.Message.MessageId,
            call,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<AgentTask>> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureDiscoveredAsync(cancellationToken, call).ConfigureAwait(false);
        var request = new GetTaskRequest { Id = taskId, HistoryLength = historyLength, Tenant = RoutingTenant };
        return await CallAsync<AgentTask>(A2AMethods.GetTask, request, WorkIqOperation.GetTask, allowRetry: true, taskId, contextId: null, messageId: null, call, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WorkIqRpcResult<AgentTask>> CancelTaskAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureDiscoveredAsync(cancellationToken, call).ConfigureAwait(false);
        var request = new CancelTaskRequest { Id = taskId, Tenant = RoutingTenant };
        return await CallAsync<AgentTask>(A2AMethods.CancelTask, request, WorkIqOperation.CancelTask, allowRetry: true, taskId, contextId: null, messageId: null, call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs discovery once (thread-safe), caches the card and creates <see cref="Client"/>; a failed attempt is retried by the next caller.</summary>
    /// <param name="cancellationToken">Cancellation token of the caller that triggers discovery.</param>
    /// <param name="call">The per-call context of the caller that triggers discovery.</param>
    internal Task<RawAgentCard> EnsureDiscoveredAsync(CancellationToken cancellationToken, WorkIqCallContext? call = null)
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
                _discovery = DiscoverCoreAsync(call, cancellationToken);
            }

            pending = _discovery;
        }

        return pending;
    }

    /// <summary>
    /// A new capture sink for one operation (from the options), under the call's capture folder. A blocking call
    /// attaches it to its own requests (<see cref="RawCaptureHandler.SinkOption"/>); a stream makes it the capture
    /// handler's <see cref="RawCaptureHandler.CurrentSink"/>, because the SDK builds the stream's request. The sink
    /// reports through this client's <see cref="Diagnostics"/>, so its <c>capture.*</c> events and issues reach the
    /// run's trace and issue files.
    /// </summary>
    /// <param name="op">The operation the capture belongs to (part of the capture id).</param>
    /// <param name="call">The per-call context (its <see cref="WorkIqCallContext.CaptureScope"/>, which already starts with the run id).</param>
    internal RawCaptureSink NewCaptureSink(WorkIqOperation op, WorkIqCallContext? call)
    {
        var sequence = Interlocked.Increment(ref _captureSequence);
        var captureId = string.Create(CultureInfo.InvariantCulture, $"{op.ToString().ToLowerInvariant()}-{sequence:D4}");
        var directory = Options.RawCaptureDirectory is null
            ? null
            : new DirectoryInfo(Path.Combine(Options.RawCaptureDirectory.FullName, call?.CaptureScope ?? Conversation.TurnContext.CaptureScopeFor(Diagnostics.RunId, "no-turn")));
        return new RawCaptureSink(captureId, Options.ContentPolicy, Options.MaxCaptureBytes, Options.MaxCaptureFrames, directory, Diagnostics);
    }

    /// <summary>Completes a capture opened by <see cref="NewCaptureSink"/>, detaches it from the handler when it is the current sink and returns its reference.</summary>
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

    /// <summary>
    /// Declares, until the returned scope is disposed, which failure reply is the answer the caller provokes on purpose
    /// (the readiness check's GetTask on a random task id → TaskNotFound; its CancelTask on a finished task →
    /// TaskNotCancelable). A matching failure is traced once as <see cref="ExpectedErrorCode"/> at Info instead of an
    /// Error-level <c>rpc.error</c> and is not registered as an issue; it is still thrown to the caller, which decides.
    /// </summary>
    /// <param name="predicate">Which failure is expected.</param>
    internal IDisposable ExpectFailure(Func<WorkIqFailure, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Volatile.Write(ref _expectedFailure, predicate);
        return new ExpectationScope(this, predicate);
    }

    /// <summary>Mints a JSON-RPC id (<c>rpc-&lt;32 hex&gt;</c>); the one place an id is minted (a turn mints its identity's id here too).</summary>
    internal static string NewRpcId() => "rpc-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The one rule for a send's configuration, blocking and streaming alike: <c>returnImmediately: false</c> (a blocking
    /// send waits for its reply; a streaming send runs to its terminal event — the flag is no streaming-finality
    /// control), and the profile's accepted output modes, the history length and the routing tenant unless the caller
    /// set them.
    /// </summary>
    /// <param name="request">The request to configure.</param>
    /// <param name="profile">The diagnostic profile whose output modes are requested.</param>
    /// <param name="historyLength">The history length; null omits it.</param>
    /// <param name="routingTenant">The routing tenant of the selected interface; null omits it.</param>
    internal static void ApplySendConfiguration(SendMessageRequest request, DiagnosticProfile profile, int? historyLength, string? routingTenant)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = request.Configuration ??= new SendMessageConfiguration();
        configuration.ReturnImmediately = false;
        configuration.AcceptedOutputModes ??= AcceptedOutputModesFor(profile);
        configuration.HistoryLength ??= historyLength;
        request.Tenant ??= routingTenant;
    }

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
            403 => (WorkIqFailureCategory.Forbidden, $"admin consent for the delegated permission is missing, the user is not in a usage-based billing plan, or Work IQ is not enabled in the tenant (scope {scope ?? Microsoft365Config.DefaultScope})"),
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

    /// <summary>
    /// Whether this client registers the issue of <paramref name="failure"/>: not when the caller registers it itself
    /// (<see cref="WorkIqCallContext.CallerRegistersIssues"/>), not for a token failure (the auth layer registered it,
    /// <see cref="WorkIqFailure.TokenFailure"/>) and not for a local cancellation (the caller stopped the call and knows why).
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="call">The per-call context.</param>
    internal static bool RegistersIssue(WorkIqFailure failure, WorkIqCallContext? call) =>
        call?.CallerRegistersIssues != true
        && failure.TokenFailure is null
        && failure.Category != WorkIqFailureCategory.Canceled;

    /// <summary>
    /// Traces <c>rpc.error</c> for a failure of a call this client ended and registers its issue when this client owns it
    /// (<see cref="RegistersIssue"/>); a failure the caller declared expected (<see cref="ExpectFailure"/>) is traced at Info only.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="rpcId">The JSON-RPC id, when any.</param>
    /// <param name="elapsedMs">Elapsed milliseconds, when measured.</param>
    /// <param name="call">The per-call context.</param>
    internal void ReportFailure(WorkIqFailure failure, WorkIqTraceStage stage, string? rpcId, double? elapsedMs, WorkIqCallContext? call = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (Volatile.Read(ref _expectedFailure) is { } expected && expected(failure))
        {
            // The caller provoked this reply on purpose (the readiness check's GetTask on a random id, its CancelTask on a
            // finished task): it is the answer the caller wants, so it is traced at Info and never registered as an issue.
            Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Info, ExpectedErrorCode, $"{failure.Category}: {failure.SafeMessage} (the expected answer to a call made on purpose; not an anomaly, not registered as an issue)")
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
            return;
        }

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

        RegisterIssue(failure, stage, call);
    }

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

    /// <summary>
    /// Registers the one issue of a failure this client ended, classified by <see cref="WorkIqIssueClassifier"/>, when
    /// <see cref="RegistersIssue"/> says this client owns it.
    /// </summary>
    private void RegisterIssue(WorkIqFailure failure, WorkIqTraceStage stage, WorkIqCallContext? call, WorkIqTraceLevel level = WorkIqTraceLevel.Error)
    {
        if (RegistersIssue(failure, call))
        {
            Diagnostics.Issue(WorkIqIssueClassifier.FromFailure(failure, stage), level);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------------------------------------

    private async Task<RawAgentCard> DiscoverCoreAsync(WorkIqCallContext? call, CancellationToken cancellationToken)
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
            throw FailDiscovery(refused, "discovery.untrusted_host", Clock.GetElapsedTime(started).TotalMilliseconds, call);
        }

        var (exchange, capture) = await FetchCardAsync(cardUri, started, call, cancellationToken).ConfigureAwait(false);
        var elapsedMs = Clock.GetElapsedTime(started).TotalMilliseconds;
        var card = ParseCard(exchange, capture, elapsedMs, call);

        var sha256 = Sha256Hex(exchange.Bytes);
        Diagnostics.Trace(
            WorkIqTraceStage.Discovery,
            "discovery.card_fetched",
            $"agent card fetched: {exchange.Bytes.Length} bytes, sha256 {sha256[..12]}…, {card.SupportedInterfaces?.Count ?? 0} interface(s), " +
            $"{card.Skills?.Count ?? 0} skill(s), streaming={Describe(card.Capabilities?.Streaming)}, pushNotifications={Describe(card.Capabilities?.PushNotifications)}",
            requestId: exchange.RequestId,
            httpStatus: exchange.HttpStatus,
            elapsedMs: elapsedMs);

        var selected = SelectInterface(card, cardUri, exchange, capture, elapsedMs, call);
        var endpoint = new Uri(selected.Url, UriKind.Absolute);
        var routingTenant = string.IsNullOrWhiteSpace(selected.Tenant) ? null : selected.Tenant;
        var raw = new RawAgentCard(card, exchange.Body, sha256, cardUri, endpoint, selected.ProtocolVersion.Trim(), exchange.ReceivedAt, routingTenant);
        Volatile.Write(ref _client, new A2AClient(endpoint, Http));
        Volatile.Write(ref _card, raw);
        Diagnostics.Trace(
            WorkIqTraceStage.Discovery,
            "discovery.interface_selected",
            $"selected {selected.ProtocolBinding} {raw.SelectedProtocolVersion} at {endpoint.Scheme}://{endpoint.Host}{endpoint.AbsolutePath} (routing tenant {(routingTenant is null ? "absent" : "present")})",
            requestId: exchange.RequestId,
            elapsedMs: Clock.GetElapsedTime(started).TotalMilliseconds);
        return raw;
    }

    /// <summary>GETs the card (bounded retry) with its own capture; a transport failure or an error status is reported and thrown.</summary>
    private async Task<(HttpExchange Exchange, RawCaptureRef Capture)> FetchCardAsync(Uri cardUri, long started, WorkIqCallContext? call, CancellationToken cancellationToken)
    {
        // The token first, outside HttpClient.Timeout: the first sign-in of a run happens here.
        var token = await AcquireTokenBeforeCallAsync(WorkIqOperation.Discover, HttpMethod.Get, cardUri, WorkIqTraceStage.Discovery, call, cancellationToken).ConfigureAwait(false);

        var sink = NewCaptureSink(WorkIqOperation.Discover, call);
        HttpExchange exchange;
        try
        {
            exchange = await ExchangeWithRetryAsync(Prepare(() => new HttpRequestMessage(HttpMethod.Get, cardUri), token, sink), WorkIqOperation.Discover, allowRetry: true, taskId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkIqProtocolException ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = ex.Failure with { Capture = ex.Failure.Capture ?? capture };
            throw FailDiscovery(failure, "discovery.failed", Clock.GetElapsedTime(started).TotalMilliseconds, call, ex);
        }
        catch (Exception ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = Classify(ex, WorkIqOperation.Discover, requestId: null, callerToken: cancellationToken, scope: Config.Scope) with { Capture = capture };
            throw FailDiscovery(failure, "discovery.failed", Clock.GetElapsedTime(started).TotalMilliseconds, call, ex);
        }

        var captureRef = EndCapture(sink);
        if (!exchange.Success)
        {
            var failure = ClassifyHttpStatus(exchange.HttpStatus, WorkIqOperation.Discover, exchange.RequestId, null, null, null, captureRef, Config.Scope, exchange.RetryAfter);
            throw FailDiscovery(failure, "discovery.failed", Clock.GetElapsedTime(started).TotalMilliseconds, call);
        }

        return (exchange, captureRef);
    }

    /// <summary>Parses the card body; a parse failure or a null card is reported and thrown.</summary>
    private AgentCard ParseCard(HttpExchange exchange, RawCaptureRef capture, double elapsedMs, WorkIqCallContext? call)
    {
        AgentCard? card;
        try
        {
            card = JsonSerializer.Deserialize<AgentCard>(exchange.Body, A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, WorkIqOperation.Discover, exchange.RequestId) with { Capture = capture, HttpStatus = exchange.HttpStatus };
            throw FailDiscovery(failure, "discovery.failed", elapsedMs, call, ex);
        }

        return card ?? throw FailDiscovery(
            new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                "Discover: the agent card body deserialized to null",
                OutcomeCertainty.Known,
                WorkIqOperation.Discover,
                HttpStatus: exchange.HttpStatus,
                RequestId: exchange.RequestId,
                Capture: capture),
            "discovery.failed",
            elapsedMs,
            call);
    }

    /// <summary>Lets the trust policy select the interface; a refusal is reported and thrown.</summary>
    private AgentInterface SelectInterface(AgentCard card, Uri cardUri, HttpExchange exchange, RawCaptureRef capture, double elapsedMs, WorkIqCallContext? call)
    {
        try
        {
            return Policy.SelectInterface(card, cardUri);
        }
        catch (WorkIqProtocolException ex)
        {
            var failure = ex.Failure with { Capture = capture, HttpStatus = exchange.HttpStatus, RequestId = exchange.RequestId };
            var code = failure.Category == WorkIqFailureCategory.UntrustedHost ? "discovery.untrusted_host" : "discovery.version_mismatch";
            throw FailDiscovery(failure, code, elapsedMs, call, ex);
        }
    }

    /// <summary>Traces a discovery failure under <paramref name="code"/>, registers its issue when this client owns it, and returns the exception to throw.</summary>
    private WorkIqProtocolException FailDiscovery(WorkIqFailure failure, string code, double elapsedMs, WorkIqCallContext? call, Exception? inner = null)
    {
        Diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Discovery, WorkIqTraceLevel.Error, code, $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}")
        {
            RequestId = failure.RequestId,
            HttpStatus = failure.HttpStatus,
            RpcErrorCode = failure.RpcErrorCode,
            ElapsedMs = elapsedMs,
        });

        RegisterIssue(failure, WorkIqTraceStage.Discovery, call);
        return new WorkIqProtocolException(failure, inner);
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
        WorkIqCallContext? call,
        CancellationToken cancellationToken)
        where T : class
    {
        var card = Card ?? await EnsureDiscoveredAsync(cancellationToken, call).ConfigureAwait(false);
        var stage = WorkIqIssueClassifier.StageFor(op);

        // The token before the call and before "rpc.sent": a sign-in that does not finish sends nothing.
        var token = await AcquireTokenBeforeCallAsync(op, HttpMethod.Post, card.SelectedEndpoint, stage, call, cancellationToken).ConfigureAwait(false);
        var rpc = new RpcCall(method, op, stage, call?.RpcId ?? NewRpcId(), taskId, contextId, call);
        var (exchange, capture, elapsedMs) = await ExchangeAsync(rpc, card.SelectedEndpoint, parameters, messageId, allowRetry, token, cancellationToken).ConfigureAwait(false);
        var result = ReadEnvelope(rpc, exchange, capture, elapsedMs);
        var value = ReadValue<T>(rpc, result, exchange, capture, elapsedMs);

        var (replyTaskId, replyContextId, replyState, replyMessageId) = DescribeResult(value);
        Diagnostics.Trace(new WorkIqTraceEvent(default, stage, WorkIqTraceLevel.Info, "rpc.response", $"{method} ← HTTP {exchange.HttpStatus}: {DescribeShape(value)}")
        {
            RpcId = rpc.RpcId,
            MessageId = replyMessageId,
            TaskId = replyTaskId ?? taskId,
            ContextId = replyContextId ?? contextId,
            RequestId = exchange.RequestId,
            HttpStatus = exchange.HttpStatus,
            A2AState = replyState,
            ElapsedMs = elapsedMs,
        });

        return new WorkIqRpcResult<T>(value, capture, exchange.RequestId, exchange.HttpStatus, exchange.ReceivedAt);
    }

    /// <summary>Posts the envelope (a safe read under the bounded retry) with its own capture; a transport failure is reported and thrown.</summary>
    private async Task<(HttpExchange Exchange, RawCaptureRef Capture, double ElapsedMs)> ExchangeAsync(
        RpcCall rpc,
        Uri endpoint,
        object parameters,
        string? messageId,
        bool allowRetry,
        string? token,
        CancellationToken cancellationToken)
    {
        var envelope = new JsonRpcRequest
        {
            Method = rpc.Method,
            Id = new JsonRpcId(rpc.RpcId),
            Params = JsonSerializer.SerializeToElement(parameters, parameters.GetType(), A2AJsonUtilities.DefaultOptions),
        };

        var started = Clock.GetTimestamp();
        Diagnostics.Trace(new WorkIqTraceEvent(default, rpc.Stage, WorkIqTraceLevel.Info, "rpc.sent", $"{rpc.Method} → {endpoint.Host}{endpoint.AbsolutePath}{(allowRetry ? "" : " (never retried)")}")
        {
            RpcId = rpc.RpcId,
            MessageId = messageId,
            TaskId = rpc.TaskId,
            ContextId = rpc.ContextId,
        });

        var sink = NewCaptureSink(rpc.Op, rpc.Call);
        HttpExchange exchange;
        try
        {
            exchange = await ExchangeWithRetryAsync(
                Prepare(() => new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new JsonRpcContent(envelope) }, token, sink),
                rpc.Op,
                allowRetry,
                rpc.TaskId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkIqProtocolException ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = ex.Failure with
            {
                Capture = ex.Failure.Capture ?? capture,
                RpcRequestId = ex.Failure.RpcRequestId ?? rpc.RpcId,
                TaskId = ex.Failure.TaskId ?? rpc.TaskId,
                ContextId = ex.Failure.ContextId ?? rpc.ContextId,
            };
            throw Fail(rpc, failure, Clock.GetElapsedTime(started).TotalMilliseconds, ex);
        }
        catch (Exception ex)
        {
            var capture = EndCapture(sink, interrupted: true);
            var failure = Classify(ex, rpc.Op, requestId: null, rpc.TaskId, rpc.ContextId, cancellationToken, Config.Scope) with { Capture = capture, RpcRequestId = rpc.RpcId };
            throw Fail(rpc, failure, Clock.GetElapsedTime(started).TotalMilliseconds, ex);
        }

        var captureRef = EndCapture(sink);
        return (exchange, captureRef, Clock.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Checks the HTTP status and the JSON-RPC envelope (an error object, no result, an A2A 0.3 shape) and returns the <c>result</c> node.</summary>
    private JsonNode ReadEnvelope(RpcCall rpc, HttpExchange exchange, RawCaptureRef capture, double elapsedMs)
    {
        if (!exchange.Success)
        {
            throw Fail(rpc, ClassifyHttpStatus(exchange.HttpStatus, rpc.Op, exchange.RequestId, rpc.RpcId, rpc.TaskId, rpc.ContextId, capture, Config.Scope, exchange.RetryAfter), elapsedMs);
        }

        JsonRpcResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<JsonRpcResponse>(exchange.Body, A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, rpc.Op, exchange.RequestId, rpc.TaskId, rpc.ContextId) with { Capture = capture, RpcRequestId = rpc.RpcId, HttpStatus = exchange.HttpStatus };
            throw Fail(rpc, UnusableReply(failure), elapsedMs, ex);
        }

        if (response is null)
        {
            throw Fail(rpc, UnusableReply(Malformed(rpc.Op, "the JSON-RPC envelope deserialized to null", exchange, rpc.RpcId, rpc.TaskId, rpc.ContextId, capture)), elapsedMs);
        }

        if (response.Error is { } error)
        {
            throw Fail(rpc, ClassifyRpcError(error.Code, rpc.Op, exchange.RequestId, rpc.RpcId, rpc.TaskId, rpc.ContextId, capture) with { HttpStatus = exchange.HttpStatus }, elapsedMs);
        }

        if (response.Id.HasValue && !string.Equals(response.Id.ToString(), rpc.RpcId, StringComparison.Ordinal))
        {
            Diagnostics.Trace(new WorkIqTraceEvent(default, rpc.Stage, WorkIqTraceLevel.Warning, "rpc.id_mismatch", $"{rpc.Method}: the reply's JSON-RPC id differs from the request id (reply correlated by HTTP exchange)")
            {
                RpcId = rpc.RpcId,
                RequestId = exchange.RequestId,
                HttpStatus = exchange.HttpStatus,
                TaskId = rpc.TaskId,
                ContextId = rpc.ContextId,
            });
        }

        if (response.Result is null)
        {
            throw Fail(rpc, UnusableReply(Malformed(rpc.Op, "the JSON-RPC envelope carries neither result nor error", exchange, rpc.RpcId, rpc.TaskId, rpc.ContextId, capture)), elapsedMs);
        }

        if (DetectLegacyShape(response.Result) is { } legacy)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.ProtocolVersionMismatch,
                $"{rpc.Op}: {legacy}; this provider speaks 1.0 only and never downgrades",
                OutcomeCertainty.Known,
                rpc.Op,
                HttpStatus: exchange.HttpStatus,
                RequestId: exchange.RequestId,
                RpcRequestId: rpc.RpcId,
                TaskId: rpc.TaskId ?? LegacyTaskId(response.Result),
                ContextId: rpc.ContextId,
                Capture: capture);
            throw Fail(rpc, UnusableReply(failure), elapsedMs);
        }

        return response.Result;
    }

    /// <summary>Deserializes and validates the typed result (one-of rules, the task id asked for).</summary>
    private T ReadValue<T>(RpcCall rpc, JsonNode result, HttpExchange exchange, RawCaptureRef capture, double elapsedMs)
        where T : class
    {
        T? value;
        try
        {
            value = result.Deserialize<T>(A2AJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            var failure = Classify(ex, rpc.Op, exchange.RequestId, rpc.TaskId, rpc.ContextId) with { Capture = capture, RpcRequestId = rpc.RpcId, HttpStatus = exchange.HttpStatus };
            throw Fail(rpc, UnusableReply(failure), elapsedMs, ex);
        }

        if (value is null)
        {
            throw Fail(rpc, UnusableReply(Malformed(rpc.Op, "the JSON-RPC result deserialized to null", exchange, rpc.RpcId, rpc.TaskId, rpc.ContextId, capture)), elapsedMs);
        }

        if (ValidateResult(value, rpc.Op, rpc.TaskId, exchange, rpc.RpcId, rpc.ContextId, capture) is { } invalid)
        {
            throw Fail(rpc, UnusableReply(invalid), elapsedMs);
        }

        return value;
    }

    /// <summary>Reports a failure of a JSON-RPC call (<see cref="ReportFailure"/>) and returns the exception to throw.</summary>
    private WorkIqProtocolException Fail(RpcCall rpc, WorkIqFailure failure, double elapsedMs, Exception? inner = null)
    {
        ReportFailure(failure, rpc.Stage, rpc.RpcId, elapsedMs, rpc.Call);
        return new WorkIqProtocolException(failure, inner);
    }

    /// <summary>
    /// A failure built from a 2xx reply this client could not use (it does not parse, has an A2A 0.3 shape or breaks a
    /// one-of rule). The service answered, so for a send it accepted the input and may have created a task: the remote
    /// state is unknown, never "no task exists". A read keeps its certainty.
    /// </summary>
    /// <param name="failure">The failure.</param>
    internal static WorkIqFailure UnusableReply(WorkIqFailure failure) =>
        failure.Operation is WorkIqOperation.Send or WorkIqOperation.Stream
            ? failure with { Certainty = OutcomeCertainty.Indeterminate }
            : failure;

    /// <summary>The task id an A2A 0.3-shaped result carries (<c>result.id</c> of a task, or <c>result.task.id</c>); null when none.</summary>
    /// <param name="result">The JSON-RPC <c>result</c> node.</param>
    private static string? LegacyTaskId(JsonNode result)
    {
        if (result is not JsonObject obj)
        {
            return null;
        }

        var task = obj["task"] as JsonObject ?? (obj.ContainsKey("status") ? obj : null);
        return task?["id"] is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
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
                Diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqIssueClassifier.StageFor(op), WorkIqTraceLevel.Warning, "rpc.task_id_mismatch", $"{op}: the reply describes another task than the one requested")
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

    /// <summary>The facts of one JSON-RPC call that every step reports with.</summary>
    private sealed record RpcCall(string Method, WorkIqOperation Op, WorkIqTraceStage Stage, string RpcId, string? TaskId, string? ContextId, WorkIqCallContext? Call);

    /// <summary>Ends an <see cref="ExpectFailure"/> declaration (only its own: a later declaration is left in place).</summary>
    private sealed class ExpectationScope : IDisposable
    {
        private WorkIqA2AClient? _owner;
        private readonly Func<WorkIqFailure, bool> _predicate;

        public ExpectationScope(WorkIqA2AClient owner, Func<WorkIqFailure, bool> predicate)
        {
            _owner = owner;
            _predicate = predicate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
            {
                Interlocked.CompareExchange(ref owner._expectedFailure, null, _predicate);
            }
        }
    }

    /// <summary>
    /// Acquires the bearer token of one operation before its HTTP call, under <paramref name="cancellationToken"/> only
    /// (never <c>HttpClient.Timeout</c>), when <see cref="Auth"/> is set; null otherwise (the auth handler then acquires
    /// it inside the pipeline). A failure has already been traced and registered by the auth handler; this adds one
    /// <c>auth.call_not_sent</c> line naming the operation and rethrows it with the operation filled in. Nothing is sent.
    /// </summary>
    /// <param name="op">The operation.</param>
    /// <param name="method">The HTTP method of the call.</param>
    /// <param name="destination">The call's URI.</param>
    /// <param name="stage">The trace stage of the operation.</param>
    /// <param name="call">The per-call context (the case and turn stamped on the auth layer's issue).</param>
    /// <param name="cancellationToken">The operation's token.</param>
    internal async Task<string?> AcquireTokenBeforeCallAsync(WorkIqOperation op, HttpMethod method, Uri destination, WorkIqTraceStage stage, WorkIqCallContext? call, CancellationToken cancellationToken)
    {
        if (Auth is null)
        {
            return null;
        }

        try
        {
            return await Auth.AcquireTokenAsync(op, WorkIqAuthHandler.DescribeDestination(method, destination), outsideHttpTimeout: true, cancellationToken, call).ConfigureAwait(false);
        }
        catch (WorkIqProtocolException ex)
        {
            var failure = ex.Failure.Operation == WorkIqOperation.Unknown ? ex.Failure with { Operation = op } : ex.Failure;
            Diagnostics.Trace(
                stage,
                "auth.call_not_sent",
                $"{op} was not sent: no bearer token ({failure.Category}: {failure.SafeMessage})",
                WorkIqTraceLevel.Error,
                httpStatus: failure.HttpStatus);
            throw new WorkIqProtocolException(failure, ex);
        }
    }

    /// <summary>
    /// Wraps a request factory so every request it builds carries its own capture sink for the capture handler and,
    /// when set, the pre-acquired token for the auth handler: both travel on the request, never in a field another
    /// call on this client could overwrite.
    /// </summary>
    /// <param name="factory">The request factory.</param>
    /// <param name="token">The pre-acquired token, or null.</param>
    /// <param name="sink">The capture sink of the operation.</param>
    private static Func<HttpRequestMessage> Prepare(Func<HttpRequestMessage> factory, string? token, RawCaptureSink sink) =>
        () =>
        {
            var request = factory();
            request.Options.Set(RawCaptureHandler.SinkOption, sink);
            if (token is not null)
            {
                request.Options.Set(WorkIqAuthHandler.PreAcquiredTokenOption, token);
            }

            return request;
        };

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
                WorkIqIssueClassifier.StageFor(op),
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
