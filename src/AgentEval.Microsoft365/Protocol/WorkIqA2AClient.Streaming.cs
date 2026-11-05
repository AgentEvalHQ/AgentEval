// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The streaming half of <see cref="WorkIqA2AClient"/>: <c>SendStreamingMessage</c> and <c>SubscribeToTask</c> over
/// the SDK's <c>SendStreamingMessageAsync</c> / <c>SubscribeToTaskAsync</c>, with every frame captured (client
/// sequence, connection epoch, receipt time, SSE envelope), exactly-one-of validation of <see cref="StreamResponse"/>,
/// task/context id consistency checks, and JSON-RPC errors inspected inside the stream. Owned by lane C (M13).
/// </summary>
/// <remarks>
/// <para>The SDK does not expose the raw SSE <c>data</c> line, the <c>id</c>/<c>event</c> fields or the JSON-RPC id
/// it generates: <see cref="WorkIqStreamFrame.DataJson"/> is therefore the typed <see cref="StreamResponse"/>
/// re-serialized with <see cref="A2AJsonUtilities.DefaultOptions"/> (a projection), <see cref="WorkIqStreamFrame.SseId"/>
/// and <see cref="WorkIqStreamFrame.SseEvent"/> are null, and no <see cref="WorkIqTraceEvent.RpcId"/> is traced. The
/// exact bytes (envelope fields included) are retained by the <see cref="RawCaptureHandler"/>'s tee under the
/// content policy; the capture reference of the last stream is <see cref="LastStreamCapture"/>.</para>
/// <para>A JSON-RPC <c>error</c> frame makes the SDK throw an <see cref="A2AException"/> out of its enumerator;
/// like any other mid-stream exception it becomes one final frame whose <see cref="WorkIqStreamFrame.Failure"/>
/// is <see cref="ClassifyException"/>'s verdict, after which the iterator ends — it never throws once a frame was
/// yielded. Discovery failures (before the stream opens) propagate like they do for the blocking operations.</para>
/// </remarks>
internal sealed partial class WorkIqA2AClient
{
    /// <summary>The output mode every profile requests first.</summary>
    internal const string TextPlainOutputMode = "text/plain";

    /// <summary>The native-evaluator-observed retrieval diagnostics mode (<see cref="DiagnosticProfile.Native118Diagnostics"/> only).</summary>
    internal const string RetrievalInfoOutputMode = "application/vnd.ms-workiq-internal.retrieval";

    /// <summary>The native-evaluator-observed developer diagnostics mode (<see cref="DiagnosticProfile.Native118Diagnostics"/> only).</summary>
    internal const string DeveloperInfoOutputMode = "application/vnd.ms-workiq.developer-info";

    /// <summary>
    /// Counts the stream connections this client opened (1 for the first <c>SendStreamingMessage</c>, +1 per
    /// <c>SubscribeToTask</c> reconnect); stamped on every frame as <see cref="WorkIqStreamFrame.ConnectionEpoch"/>.
    /// Declared here (not in the constructor file) because only the streaming operations touch it.
    /// </summary>
    private int _streamConnectionEpoch;

    /// <summary>The connection epoch of the most recently opened stream (0 before any).</summary>
    internal int StreamConnectionEpoch => Volatile.Read(ref _streamConnectionEpoch);

    /// <summary>
    /// The raw capture reference of the most recently ended stream (<c>SendStreamingMessage</c> or
    /// <c>SubscribeToTask</c>); null while a stream is open or before any. Frames carry no capture slot, so the
    /// conversation reads it here once the enumeration ends and attaches it to the reduced snapshot.
    /// </summary>
    internal RawCaptureRef? LastStreamCapture { get; private set; }

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> StreamAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Message);
        return RunStreamAsync(WorkIqOperation.Stream, request, subscribedTaskId: null, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> SubscribeAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return RunStreamAsync(WorkIqOperation.Subscribe, request: null, taskId, cancellationToken);
    }

    /// <summary>
    /// The accepted output modes a profile requests: <c>text/plain</c> for <see cref="DiagnosticProfile.PublicAnswerOnly"/>;
    /// additionally the two native-evaluator-observed diagnostic modes for <see cref="DiagnosticProfile.Native118Diagnostics"/>
    /// (<c>text/plain</c> first, no duplicates — the shape the native writer was observed to send).
    /// </summary>
    /// <param name="profile">The profile.</param>
    internal static List<string> AcceptedOutputModesFor(DiagnosticProfile profile) => profile switch
    {
        DiagnosticProfile.Native118Diagnostics => [TextPlainOutputMode, RetrievalInfoOutputMode, DeveloperInfoOutputMode],
        _ => [TextPlainOutputMode],
    };

    /// <summary>
    /// Sets the request configuration the way the blocking send does (kept local to this partial on purpose):
    /// <c>acceptedOutputModes</c> from the profile, <c>returnImmediately: false</c> (a streaming send runs to its
    /// terminal event; the flag is not a streaming-finality control) and <c>historyLength</c> from the options
    /// unless the caller already set one.
    /// </summary>
    /// <param name="request">The request to configure.</param>
    /// <param name="profile">The effective diagnostic profile.</param>
    /// <param name="historyLength">The configured history length; null omits it.</param>
    private static void ApplyStreamConfiguration(SendMessageRequest request, DiagnosticProfile profile, int? historyLength)
    {
        var configuration = request.Configuration ?? new SendMessageConfiguration();
        configuration.AcceptedOutputModes = AcceptedOutputModesFor(profile);
        configuration.ReturnImmediately = false;
        configuration.HistoryLength ??= historyLength;
        request.Configuration = configuration;
    }

    /// <summary>The routing <c>tenant</c> of the interface the trust policy selected, when the card declares one.</summary>
    /// <param name="card">The discovered card.</param>
    private static string? ResolveRoutingTenant(RawAgentCard card)
    {
        foreach (var candidate in card.Card.SupportedInterfaces ?? [])
        {
            if (candidate is null || string.IsNullOrEmpty(candidate.Url))
            {
                continue;
            }

            if (string.Equals(candidate.Url, card.SelectedEndpoint.AbsoluteUri, StringComparison.Ordinal)
                || string.Equals(candidate.Url, card.SelectedEndpoint.OriginalString, StringComparison.Ordinal)
                || (Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url) && url == card.SelectedEndpoint))
            {
                return string.IsNullOrWhiteSpace(candidate.Tenant) ? null : candidate.Tenant;
            }
        }

        return null;
    }

    private async IAsyncEnumerable<WorkIqStreamFrame> RunStreamAsync(
        WorkIqOperation operation,
        SendMessageRequest? request,
        string? subscribedTaskId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var isSubscribe = operation == WorkIqOperation.Subscribe;
        var stage = isSubscribe ? WorkIqTraceStage.Subscribe : WorkIqTraceStage.Stream;
        var prefix = isSubscribe ? "subscribe" : "stream";

        // Discovery failures propagate before any frame exists, exactly like the blocking operations.
        var card = await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);
        var routingTenant = ResolveRoutingTenant(card);
        var expectedTaskId = subscribedTaskId ?? request?.Message.TaskId;
        var expectedContextId = request?.Message.ContextId;
        var profile = Options.ProfileOverride ?? Config.ResolveProfile();
        if (request is not null)
        {
            ApplyStreamConfiguration(request, profile, Options.HistoryLength);
            request.Tenant ??= routingTenant;
        }

        var epoch = Interlocked.Increment(ref _streamConnectionEpoch);
        var startTimestamp = Clock.GetTimestamp();
        var tracker = new StreamIdTracker(expectedTaskId, expectedContextId);
        var sink = BeginCapture(operation);
        LastStreamCapture = null;
        RawCaptureRef? capture = null;
        var captureEnded = false;
        long sequence = 0;
        var malformed = 0;
        Exception? terminalException = null;
        IAsyncEnumerator<StreamResponse>? enumerator = null;

        TraceEvent(
            stage,
            prefix + ".opened",
            isSubscribe ? $"SubscribeToTask stream opened (epoch {epoch})" : $"SendStreamingMessage stream opened (epoch {epoch})",
            WorkIqTraceLevel.Info,
            new OpenedData(epoch, operation.ToString(), isSubscribe ? null : AcceptedOutputModesFor(profile), routingTenant is not null, card.SelectedEndpoint.Host),
            taskId: expectedTaskId,
            contextId: expectedContextId,
            messageId: request?.Message.MessageId);

        try
        {
            try
            {
                var source = isSubscribe
                    ? Client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = subscribedTaskId!, Tenant = routingTenant }, cancellationToken)
                    : Client.SendStreamingMessageAsync(request!, cancellationToken);
                enumerator = source.GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                terminalException = ex;
            }

            while (terminalException is null)
            {
                StreamResponse? response = null;
                var moved = false;
                try
                {
                    moved = await enumerator!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        response = enumerator.Current;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    terminalException = ex;
                }

                if (terminalException is not null || !moved)
                {
                    break;
                }

                if (response is null)
                {
                    // The SDK throws rather than yielding null; a null here is a defect worth a loud, typed failure.
                    terminalException = new InvalidOperationException("The A2A SDK stream yielded a null StreamResponse.");
                    break;
                }

                sequence++;
                var frame = BuildFrame(response, sequence, epoch, tracker, stage, prefix, operation);
                if (frame.Failure is not null)
                {
                    malformed++;
                }

                yield return frame;
            }

            if (terminalException is not null)
            {
                capture = TryEndCapture(sink, stage, prefix, tracker, interrupted: true);
                captureEnded = true;
                sequence++;

                // The static classifier (behind ClassifyException) is used directly so the caller's token tells a local
                // cancellation from an HttpClient timeout: both surface as TaskCanceledException mid-stream.
                var failure = Classify(terminalException, operation, requestId: null, tracker.TaskId, tracker.ContextId, callerToken: cancellationToken, scope: Config.Scope);
                if (failure.Capture is null && capture is not null)
                {
                    failure = failure with { Capture = capture };
                }

                TraceEvent(
                    stage,
                    prefix + ".failed",
                    $"{(isSubscribe ? "SubscribeToTask" : "SendStreamingMessage")} stream failed after {sequence - 1} frame(s): {failure.Category}: {failure.SafeMessage}",
                    WorkIqTraceLevel.Error,
                    new FailedData(failure.Category.ToString(), failure.Certainty.ToString(), terminalException.GetType().FullName ?? terminalException.GetType().Name, sequence - 1, epoch),
                    taskId: tracker.TaskId,
                    contextId: tracker.ContextId,
                    httpStatus: failure.HttpStatus,
                    rpcErrorCode: failure.RpcErrorCode,
                    requestId: failure.RequestId,
                    elapsedMs: Clock.GetElapsedTime(startTimestamp).TotalMilliseconds);
                Diagnostics.Issue(
                    new WorkIqIssue(default, stage, IssueCategoryFor(failure.Category), prefix + ".failed", $"the stream failed after {sequence - 1} frame(s): {failure.Category}: {failure.SafeMessage}", SuggestedCauseFor(failure, sequence - 1))
                    {
                        TaskId = tracker.TaskId,
                        ContextId = tracker.ContextId,
                        HttpStatus = failure.HttpStatus,
                        RpcErrorCode = failure.RpcErrorCode,
                        RequestId = failure.RequestId,
                        A2AState = failure.RemoteTaskState,
                    });

                yield return new WorkIqStreamFrame(sequence, epoch, Clock.GetUtcNow(), null, null, null, null, failure);
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    TraceEvent(stage, prefix + ".dispose_failed", $"disposing the SDK stream enumerator failed: {ex.GetType().Name}", WorkIqTraceLevel.Warning, null, taskId: tracker.TaskId, contextId: tracker.ContextId);
                }
            }

            if (!captureEnded)
            {
                // A clean EOF completes the capture; the reducer, not the capture, decides whether EOF came too early.
                capture = TryEndCapture(sink, stage, prefix, tracker, interrupted: false);
            }

            LastStreamCapture = capture;
            var elapsed = Clock.GetElapsedTime(startTimestamp).TotalMilliseconds;
            TraceEvent(
                stage,
                prefix + ".ended",
                terminalException is null
                    ? $"stream ended after {sequence} frame(s){(malformed > 0 ? $", {malformed} malformed" : "")} (EOF is not completion)"
                    : $"stream ended by a failure after {sequence} frame(s) including the failure frame{(malformed > 0 ? $", {malformed} malformed" : "")}",
                terminalException is null && malformed == 0 ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning,
                new EndedData(sequence, malformed, terminalException is not null, epoch, tracker.TaskId, tracker.ContextId, tracker.LastState, capture?.State.ToString(), capture?.ByteCount),
                taskId: tracker.TaskId,
                contextId: tracker.ContextId,
                a2aState: tracker.LastState,
                elapsedMs: elapsed);
        }
    }

    private WorkIqStreamFrame BuildFrame(
        StreamResponse response,
        long sequence,
        int epoch,
        StreamIdTracker tracker,
        WorkIqTraceStage stage,
        string prefix,
        WorkIqOperation operation)
    {
        var receivedAt = Clock.GetUtcNow();
        string? dataJson = null;
        try
        {
            dataJson = JsonSerializer.Serialize(response, A2AJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            TraceEvent(stage, prefix + ".projection_failed", $"frame {sequence} could not be re-serialized: {ex.GetType().Name}", WorkIqTraceLevel.Warning, null, taskId: tracker.TaskId, contextId: tracker.ContextId);
        }

        var bytes = dataJson is null ? (int?)null : Encoding.UTF8.GetByteCount(dataJson);
        var populated = (response.Task is null ? 0 : 1) + (response.Message is null ? 0 : 1) + (response.StatusUpdate is null ? 0 : 1) + (response.ArtifactUpdate is null ? 0 : 1);
        if (populated != 1)
        {
            var failure = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                $"stream frame {sequence} has {populated} of task|message|statusUpdate|artifactUpdate populated; exactly one is required",
                OutcomeCertainty.Indeterminate,
                operation,
                TaskId: tracker.TaskId,
                ContextId: tracker.ContextId);
            TraceEvent(
                stage,
                prefix + ".malformed",
                failure.SafeMessage,
                WorkIqTraceLevel.Warning,
                new FrameData(sequence, epoch, "malformed", null, null, null, null, bytes, populated),
                taskId: tracker.TaskId,
                contextId: tracker.ContextId);
            Diagnostics.Issue(
                new WorkIqIssue(default, stage, WorkIqIssueCategory.Protocol, prefix + ".one_of_violation", failure.SafeMessage,
                    "The service sent a frame whose result holds several (or none) of task | message | statusUpdate | artifactUpdate. A2A 1.0 requires exactly one; the frame is recorded as malformed, not applied and not skipped silently. The raw capture holds the exact bytes.")
                {
                    TaskId = tracker.TaskId,
                    ContextId = tracker.ContextId,
                },
                WorkIqTraceLevel.Warning);
            return new WorkIqStreamFrame(sequence, epoch, receivedAt, null, dataJson, null, null, failure);
        }

        var described = Describe(response);
        foreach (var mismatch in tracker.Observe(described.TaskId, described.ContextId))
        {
            TraceEvent(stage, prefix + ".id_mismatch", $"frame {sequence}: {mismatch}", WorkIqTraceLevel.Warning, new FrameData(sequence, epoch, described.Kind, described.TaskId, described.ContextId, described.ArtifactId, described.State, bytes, populated), taskId: described.TaskId, contextId: described.ContextId);
            Diagnostics.Issue(
                new WorkIqIssue(default, stage, WorkIqIssueCategory.Protocol, prefix + ".id_mismatch", $"frame {sequence}: {mismatch}",
                    "A frame carried a task or context id different from the one this stream established. The frame is delivered as is and the reducer records the mismatch; the service may be multiplexing or the subscription may target another task.")
                {
                    TaskId = described.TaskId,
                    ContextId = described.ContextId,
                },
                WorkIqTraceLevel.Warning);
        }

        if (described.State is not null)
        {
            tracker.LastState = described.State;
        }

        TraceEvent(
            stage,
            prefix + ".frame",
            $"frame {sequence} ({described.Kind}{(described.ArtifactId is null ? "" : $" {described.ArtifactId}")}{(described.State is null ? "" : $" {described.State}")}{(bytes is null ? "" : $", {bytes} bytes")})",
            WorkIqTraceLevel.Info,
            new FrameData(sequence, epoch, described.Kind, described.TaskId, described.ContextId, described.ArtifactId, described.State, bytes, populated, described.Append, described.LastChunk),
            taskId: described.TaskId ?? tracker.TaskId,
            contextId: described.ContextId ?? tracker.ContextId,
            messageId: described.MessageId,
            a2aState: described.State);

        if (described.State is not null)
        {
            TraceEvent(
                stage,
                "task.state",
                $"task state observed in frame {sequence}: {described.State}",
                WorkIqTraceLevel.Info,
                null,
                taskId: described.TaskId ?? tracker.TaskId,
                contextId: described.ContextId ?? tracker.ContextId,
                a2aState: described.State);
        }

        return new WorkIqStreamFrame(sequence, epoch, receivedAt, response, dataJson, null, null, null);
    }

    private RawCaptureRef? TryEndCapture(RawCaptureSink sink, WorkIqTraceStage stage, string prefix, StreamIdTracker tracker, bool interrupted)
    {
        try
        {
            return EndCapture(sink, interrupted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceEvent(stage, prefix + ".capture_failed", $"ending the raw capture failed: {ex.GetType().Name}: {ex.Message}", WorkIqTraceLevel.Error, null, taskId: tracker.TaskId, contextId: tracker.ContextId);
            Diagnostics.Issue(
                new WorkIqIssue(default, WorkIqTraceStage.Capture, WorkIqIssueCategory.Capture, prefix + ".capture_failed", $"the raw capture of the stream could not be finalised: {ex.GetType().Name}: {ex.Message}",
                    "The capture sink failed while closing (a full disk, an unwritable RawCaptureDirectory, or a bound exceeded). The frames were still delivered; the evidence carries no raw capture for this stream.")
                {
                    TaskId = tracker.TaskId,
                    ContextId = tracker.ContextId,
                });
            return null;
        }
    }

    private static FrameDescription Describe(StreamResponse response)
    {
        switch (response.PayloadCase)
        {
            case StreamResponseCase.Task:
            {
                var task = response.Task!;
                return new FrameDescription("task", task.Id, task.ContextId, null, WorkIqSdkMapping.ToRawState(task.Status?.State ?? TaskState.Unspecified), null, null, task.Status?.Message?.MessageId);
            }

            case StreamResponseCase.Message:
            {
                var message = response.Message!;
                return new FrameDescription("message", message.TaskId, message.ContextId, null, null, null, null, message.MessageId);
            }

            case StreamResponseCase.StatusUpdate:
            {
                var update = response.StatusUpdate!;
                return new FrameDescription("statusUpdate", update.TaskId, update.ContextId, null, WorkIqSdkMapping.ToRawState(update.Status?.State ?? TaskState.Unspecified), null, null, update.Status?.Message?.MessageId);
            }

            case StreamResponseCase.ArtifactUpdate:
            {
                var update = response.ArtifactUpdate!;
                return new FrameDescription("artifactUpdate", update.TaskId, update.ContextId, update.Artifact?.ArtifactId, null, update.Append, update.LastChunk, null);
            }

            default:
                return new FrameDescription("none", null, null, null, null, null, null, null);
        }
    }

    private void TraceEvent(
        WorkIqTraceStage stage,
        string code,
        string message,
        WorkIqTraceLevel level,
        object? data,
        string? taskId = null,
        string? contextId = null,
        string? messageId = null,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        string? requestId = null,
        string? a2aState = null,
        double? elapsedMs = null)
    {
        if (Diagnostics.IsNone)
        {
            return;
        }

        Diagnostics.Trace(new WorkIqTraceEvent(default, stage, level, code, message)
        {
            TaskId = taskId,
            ContextId = contextId,
            MessageId = messageId,
            HttpStatus = httpStatus,
            RpcErrorCode = rpcErrorCode,
            RequestId = requestId,
            A2AState = a2aState,
            ElapsedMs = elapsedMs,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, data.GetType(), WorkIqDiagnosticsJson.Options),
        });
    }

    private static WorkIqIssueCategory IssueCategoryFor(WorkIqFailureCategory category) => category switch
    {
        WorkIqFailureCategory.Unauthorized => WorkIqIssueCategory.Authentication,
        WorkIqFailureCategory.Forbidden => WorkIqIssueCategory.Permission,
        WorkIqFailureCategory.Transport or WorkIqFailureCategory.Timeout => WorkIqIssueCategory.Network,
        WorkIqFailureCategory.ProtocolVersionMismatch or WorkIqFailureCategory.Malformed => WorkIqIssueCategory.Protocol,
        WorkIqFailureCategory.UntrustedHost => WorkIqIssueCategory.Config,
        WorkIqFailureCategory.RateLimited or WorkIqFailureCategory.NotFound or WorkIqFailureCategory.RemoteError => WorkIqIssueCategory.Service,
        _ => WorkIqIssueCategory.Unknown,
    };

    private static string SuggestedCauseFor(WorkIqFailure failure, long framesBefore)
    {
        var after = framesBefore == 0 ? "before any frame arrived" : $"after {framesBefore} frame(s)";
        return failure.Category switch
        {
            WorkIqFailureCategory.RemoteError => $"The service answered with a JSON-RPC error inside the stream {after} (code {failure.RpcErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}). The task's remote state is unknown from the stream; when a task id is known the turn reconciles with GetTask.",
            WorkIqFailureCategory.Unauthorized => "The token was rejected (HTTP 401) when the stream was opened. Sign in again (device code); the request is never resent automatically.",
            WorkIqFailureCategory.Forbidden => "The signed-in principal lacks the delegated permission or admin consent for Work IQ (HTTP 403).",
            WorkIqFailureCategory.RateLimited => "The service throttled the request (HTTP 429/503). A streaming send is never retried; wait and run the case again.",
            WorkIqFailureCategory.NotFound => "The task or endpoint was not found (HTTP 404 / TaskNotFound); a not-found task may be expired, purged or inaccessible rather than nonexistent.",
            WorkIqFailureCategory.Timeout => $"The local timeout fired {after}. The remote task may still be running; when its id is known the turn reconciles with GetTask.",
            WorkIqFailureCategory.Transport => $"The connection dropped {after} (DNS, TLS, socket or proxy). When a task id is known the turn reconciles with GetTask/SubscribeToTask; otherwise the outcome is indeterminate.",
            WorkIqFailureCategory.Canceled => $"The caller's cancellation token fired {after}. Only the local wait stopped; the remote task was not cancelled unless CancelTask confirms it.",
            WorkIqFailureCategory.Malformed => $"The stream carried bytes the SDK could not parse {after}. The raw capture holds them; the run cannot pass on a parse failure.",
            WorkIqFailureCategory.ProtocolVersionMismatch => "The service answered in a shape this provider does not speak (A2A 0.3 or VersionNotSupported). Check the A2A-Version header and the card's interfaces.",
            _ => $"The stream failed {after}: {failure.SafeMessage}. The raw capture holds what was received.",
        };
    }

    /// <summary>Tracks the task/context ids a stream established (from the request, then from the first frame that carries them) and reports frames that disagree.</summary>
    private sealed class StreamIdTracker
    {
        public StreamIdTracker(string? expectedTaskId, string? expectedContextId)
        {
            TaskId = string.IsNullOrEmpty(expectedTaskId) ? null : expectedTaskId;
            ContextId = string.IsNullOrEmpty(expectedContextId) ? null : expectedContextId;
        }

        public string? TaskId { get; private set; }

        public string? ContextId { get; private set; }

        public string? LastState { get; set; }

        public IReadOnlyList<string> Observe(string? taskId, string? contextId)
        {
            List<string>? mismatches = null;
            if (!string.IsNullOrEmpty(taskId))
            {
                if (TaskId is null)
                {
                    TaskId = taskId;
                }
                else if (!string.Equals(TaskId, taskId, StringComparison.Ordinal))
                {
                    (mismatches ??= []).Add($"task id '{taskId}' differs from the stream's task '{TaskId}'");
                }
            }

            if (!string.IsNullOrEmpty(contextId))
            {
                if (ContextId is null)
                {
                    ContextId = contextId;
                }
                else if (!string.Equals(ContextId, contextId, StringComparison.Ordinal))
                {
                    (mismatches ??= []).Add($"context id '{contextId}' differs from the stream's context '{ContextId}'");
                }
            }

            return mismatches ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
    }

    private sealed record FrameDescription(string Kind, string? TaskId, string? ContextId, string? ArtifactId, string? State, bool? Append, bool? LastChunk, string? MessageId);

    private sealed record OpenedData(int Epoch, string Operation, List<string>? AcceptedOutputModes, bool RoutingTenantSent, string Host);

    private sealed record FrameData(long Sequence, int Epoch, string Kind, string? TaskId, string? ContextId, string? ArtifactId, string? State, int? Bytes, int Populated, bool? Append = null, bool? LastChunk = null);

    private sealed record FailedData(string Category, string Certainty, string ExceptionType, long FramesBefore, int Epoch);

    private sealed record EndedData(long Frames, int Malformed, bool FailedStream, int Epoch, string? TaskId, string? ContextId, string? LastState, string? CaptureState, long? CaptureBytes);
}
