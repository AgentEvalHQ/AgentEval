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
/// task/context id consistency checks, and JSON-RPC errors inspected inside the stream.
/// </summary>
/// <remarks>
/// <para>The SDK does not expose the raw SSE <c>data</c> line, the <c>id</c>/<c>event</c> fields or the JSON-RPC id
/// it generates: <see cref="WorkIqStreamFrame.DataJson"/> is therefore the typed <see cref="StreamResponse"/>
/// re-serialized with <see cref="A2AJsonUtilities.DefaultOptions"/> (a projection), <see cref="WorkIqStreamFrame.SseId"/>
/// and <see cref="WorkIqStreamFrame.SseEvent"/> are null, and no <see cref="WorkIqTraceEvent.RpcId"/> is traced. The
/// exact bytes (envelope fields included) are retained by the <see cref="RawCaptureHandler"/>'s tee under the
/// content policy; the capture reference of the stream is handed back on <see cref="WorkIqCallContext.StreamCapture"/>.</para>
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

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> StreamAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Message);
        return RunStreamAsync(WorkIqOperation.Stream, request, subscribedTaskId: null, call, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> SubscribeAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return RunStreamAsync(WorkIqOperation.Subscribe, request: null, taskId, call, cancellationToken);
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

    private async IAsyncEnumerable<WorkIqStreamFrame> RunStreamAsync(
        WorkIqOperation operation,
        SendMessageRequest? request,
        string? subscribedTaskId,
        WorkIqCallContext? call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Discovery failures propagate before any frame exists, exactly like the blocking operations.
        var card = await EnsureDiscoveredAsync(cancellationToken, call).ConfigureAwait(false);

        // So does a sign-in that does not finish: the token is acquired before the stream opens, outside
        // HttpClient.Timeout (the SDK builds its own requests, so the auth handler's own call is then a cache hit).
        _ = await AcquireTokenBeforeCallAsync(operation, HttpMethod.Post, card.SelectedEndpoint, WorkIqIssueClassifier.StageFor(operation), call, cancellationToken).ConfigureAwait(false);
        if (request is not null)
        {
            ApplySendConfiguration(request, Profile, Options.HistoryLength, RoutingTenant);
        }

        var run = new StreamRun(
            operation,
            subscribedTaskId ?? request?.Message.TaskId,
            request?.Message.ContextId,
            Interlocked.Increment(ref _streamConnectionEpoch),
            Clock.GetTimestamp(),
            NewCaptureSink(operation, call));

        // The SDK builds the stream's request, so its capture sink cannot travel on it: the handler's current sink it is.
        Capture.CurrentSink = run.Sink;
        TraceOpened(run, card, request);

        IAsyncEnumerator<StreamResponse>? enumerator = null;
        try
        {
            try
            {
                var source = run.IsSubscribe
                    ? Client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = subscribedTaskId!, Tenant = RoutingTenant }, cancellationToken)
                    : Client.SendStreamingMessageAsync(request!, cancellationToken);
                enumerator = source.GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                run.TerminalException = ex;
            }

            while (run.TerminalException is null && await NextResponseAsync(enumerator!, run).ConfigureAwait(false) is { } response)
            {
                run.Sequence++;
                yield return BuildFrame(response, run, call);
            }

            if (run.TerminalException is not null)
            {
                yield return FailureFrame(run, call, cancellationToken);
            }
        }
        finally
        {
            await DisposeEnumeratorAsync(enumerator, run).ConfigureAwait(false);
            EndStream(run, call);
        }
    }

    /// <summary>The next response of the SDK stream; null at its end or when it failed (then <see cref="StreamRun.TerminalException"/> is set).</summary>
    private static async Task<StreamResponse?> NextResponseAsync(IAsyncEnumerator<StreamResponse> enumerator, StreamRun run)
    {
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return null;
            }

            // The SDK throws rather than yielding null; a null here is a defect worth a loud, typed failure.
            return enumerator.Current ?? throw new InvalidOperationException("The A2A SDK stream yielded a null StreamResponse.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.TerminalException = ex;
            return null;
        }
    }

    private void TraceOpened(StreamRun run, RawAgentCard card, SendMessageRequest? request) =>
        TraceEvent(
            run.Stage,
            run.Prefix + ".opened",
            run.IsSubscribe ? $"SubscribeToTask stream opened (epoch {run.Epoch})" : $"SendStreamingMessage stream opened (epoch {run.Epoch})",
            WorkIqTraceLevel.Info,
            new OpenedData(run.Epoch, run.Operation.ToString(), request?.Configuration?.AcceptedOutputModes?.ToList(), RoutingTenant is not null, card.SelectedEndpoint.Host),
            taskId: run.Tracker.TaskId,
            contextId: run.Tracker.ContextId,
            messageId: request?.Message.MessageId);

    /// <summary>
    /// Ends the capture as interrupted and turns the exception that ended the stream into its one final failure frame:
    /// traced, and registered as an issue when this client ends the operation (<see cref="RegistersIssue"/>).
    /// </summary>
    private WorkIqStreamFrame FailureFrame(StreamRun run, WorkIqCallContext? call, CancellationToken cancellationToken)
    {
        var exception = run.TerminalException!;
        run.Capture = TryEndCapture(run, interrupted: true);
        run.CaptureEnded = true;
        run.Sequence++;
        var framesBefore = run.Sequence - 1;

        // The static classifier (behind ClassifyException) is used directly so the caller's token tells a local
        // cancellation from an HttpClient timeout: both surface as TaskCanceledException mid-stream.
        var failure = Classify(exception, run.Operation, requestId: null, run.Tracker.TaskId, run.Tracker.ContextId, callerToken: cancellationToken, scope: Config.Scope);
        if (exception is JsonException)
        {
            // A frame of an accepted stream that does not parse: the service answered, so a streamed send may have created a task.
            failure = UnusableReply(failure);
        }

        if (failure.Capture is null && run.Capture is not null)
        {
            failure = failure with { Capture = run.Capture };
        }

        TraceEvent(
            run.Stage,
            run.Prefix + ".failed",
            $"{(run.IsSubscribe ? "SubscribeToTask" : "SendStreamingMessage")} stream failed after {framesBefore} frame(s): {failure.Category}: {failure.SafeMessage}",
            WorkIqTraceLevel.Error,
            new FailedData(failure.Category.ToString(), failure.Certainty.ToString(), exception.GetType().FullName ?? exception.GetType().Name, framesBefore, run.Epoch),
            taskId: run.Tracker.TaskId,
            contextId: run.Tracker.ContextId,
            httpStatus: failure.HttpStatus,
            rpcErrorCode: failure.RpcErrorCode,
            requestId: failure.RequestId,
            elapsedMs: Clock.GetElapsedTime(run.Started).TotalMilliseconds);
        RegisterIssue(failure, run.Stage, call);

        return new WorkIqStreamFrame(run.Sequence, run.Epoch, Clock.GetUtcNow(), null, null, null, null, failure);
    }

    private async Task DisposeEnumeratorAsync(IAsyncEnumerator<StreamResponse>? enumerator, StreamRun run)
    {
        if (enumerator is null)
        {
            return;
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceEvent(run.Stage, run.Prefix + ".dispose_failed", $"disposing the SDK stream enumerator failed: {ex.GetType().Name}", WorkIqTraceLevel.Warning, null, taskId: run.Tracker.TaskId, contextId: run.Tracker.ContextId);
        }
    }

    /// <summary>Completes the capture (unless the failure frame did), hands it back on the call context and traces the end of the stream.</summary>
    private void EndStream(StreamRun run, WorkIqCallContext? call)
    {
        if (!run.CaptureEnded)
        {
            // A clean EOF completes the capture; the reducer, not the capture, decides whether EOF came too early.
            run.Capture = TryEndCapture(run, interrupted: false);
        }

        if (call is not null)
        {
            call.StreamCapture = run.Capture;
        }

        var failed = run.TerminalException is not null;
        TraceEvent(
            run.Stage,
            run.Prefix + ".ended",
            failed
                ? $"stream ended by a failure after {run.Sequence} frame(s) including the failure frame{(run.Malformed > 0 ? $", {run.Malformed} malformed" : "")}"
                : $"stream ended after {run.Sequence} frame(s){(run.Malformed > 0 ? $", {run.Malformed} malformed" : "")} (EOF is not completion)",
            !failed && run.Malformed == 0 ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning,
            new EndedData(run.Sequence, run.Malformed, failed, run.Epoch, run.Tracker.TaskId, run.Tracker.ContextId, run.Tracker.LastState, run.Capture?.State.ToString(), run.Capture?.ByteCount),
            taskId: run.Tracker.TaskId,
            contextId: run.Tracker.ContextId,
            a2aState: run.Tracker.LastState,
            elapsedMs: Clock.GetElapsedTime(run.Started).TotalMilliseconds);
    }

    private WorkIqStreamFrame BuildFrame(StreamResponse response, StreamRun run, WorkIqCallContext? call)
    {
        var sequence = run.Sequence;
        var tracker = run.Tracker;
        var receivedAt = Clock.GetUtcNow();
        string? dataJson = null;
        try
        {
            dataJson = JsonSerializer.Serialize(response, A2AJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            TraceEvent(run.Stage, run.Prefix + ".projection_failed", $"frame {sequence} could not be re-serialized: {ex.GetType().Name}", WorkIqTraceLevel.Warning, null, taskId: tracker.TaskId, contextId: tracker.ContextId);
        }

        var bytes = dataJson is null ? (int?)null : Encoding.UTF8.GetByteCount(dataJson);
        var populated = (response.Task is null ? 0 : 1) + (response.Message is null ? 0 : 1) + (response.StatusUpdate is null ? 0 : 1) + (response.ArtifactUpdate is null ? 0 : 1);
        if (populated != 1)
        {
            return MalformedFrame(run, call, receivedAt, dataJson, bytes, populated);
        }

        var described = Describe(response);
        ReportIdMismatches(run, described, bytes, populated);
        if (described.State is not null)
        {
            tracker.LastState = described.State;
        }

        TraceEvent(
            run.Stage,
            run.Prefix + ".frame",
            $"frame {sequence} ({described.Kind}{(described.ArtifactId is null ? "" : $" {described.ArtifactId}")}{(described.State is null ? "" : $" {described.State}")}{(bytes is null ? "" : $", {bytes} bytes")})",
            WorkIqTraceLevel.Info,
            new FrameData(sequence, run.Epoch, described.Kind, described.TaskId, described.ContextId, described.ArtifactId, described.State, bytes, populated, described.Append, described.LastChunk),
            taskId: described.TaskId ?? tracker.TaskId,
            contextId: described.ContextId ?? tracker.ContextId,
            messageId: described.MessageId,
            a2aState: described.State);

        if (described.State is not null)
        {
            TraceEvent(
                run.Stage,
                "task.state",
                $"task state observed in frame {sequence}: {described.State}",
                WorkIqTraceLevel.Info,
                null,
                taskId: described.TaskId ?? tracker.TaskId,
                contextId: described.ContextId ?? tracker.ContextId,
                a2aState: described.State);
        }

        return new WorkIqStreamFrame(sequence, run.Epoch, receivedAt, response, dataJson, null, null, null);
    }

    /// <summary>
    /// The frame of a response that violates exactly-one-of: recorded as malformed, never applied and never skipped
    /// silently. A turn that reads it stops and registers the issue of its outcome, so this client registers one only
    /// for a stream of its own.
    /// </summary>
    private WorkIqStreamFrame MalformedFrame(StreamRun run, WorkIqCallContext? call, DateTimeOffset receivedAt, string? dataJson, int? bytes, int populated)
    {
        run.Malformed++;
        var failure = new WorkIqFailure(
            WorkIqFailureCategory.Malformed,
            $"stream frame {run.Sequence} has {populated} of task|message|statusUpdate|artifactUpdate populated; exactly one is required",
            OutcomeCertainty.Indeterminate,
            run.Operation,
            TaskId: run.Tracker.TaskId,
            ContextId: run.Tracker.ContextId);
        TraceEvent(
            run.Stage,
            run.Prefix + ".malformed",
            failure.SafeMessage,
            WorkIqTraceLevel.Warning,
            new FrameData(run.Sequence, run.Epoch, "malformed", null, null, null, null, bytes, populated),
            taskId: run.Tracker.TaskId,
            contextId: run.Tracker.ContextId);
        RegisterIssue(failure, run.Stage, call, WorkIqTraceLevel.Warning);
        return new WorkIqStreamFrame(run.Sequence, run.Epoch, receivedAt, null, dataJson, null, null, failure);
    }

    /// <summary>Traces and registers every task or context id of the frame that disagrees with the ids the stream established.</summary>
    private void ReportIdMismatches(StreamRun run, FrameDescription described, int? bytes, int populated)
    {
        foreach (var mismatch in run.Tracker.Observe(described.TaskId, described.ContextId))
        {
            TraceEvent(run.Stage, run.Prefix + ".id_mismatch", $"frame {run.Sequence}: {mismatch}", WorkIqTraceLevel.Warning, new FrameData(run.Sequence, run.Epoch, described.Kind, described.TaskId, described.ContextId, described.ArtifactId, described.State, bytes, populated), taskId: described.TaskId, contextId: described.ContextId);
            Diagnostics.Issue(
                new WorkIqIssue(default, run.Stage, WorkIqIssueCategory.Protocol, run.Prefix + ".id_mismatch", $"frame {run.Sequence}: {mismatch}",
                    "A frame carried a task or context id different from the one this stream established. The frame is delivered as is and the reducer records the mismatch; the service may be multiplexing or the subscription may target another task.")
                {
                    TaskId = described.TaskId,
                    ContextId = described.ContextId,
                },
                WorkIqTraceLevel.Warning);
        }
    }

    private RawCaptureRef? TryEndCapture(StreamRun run, bool interrupted)
    {
        try
        {
            return EndCapture(run.Sink, interrupted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceEvent(run.Stage, run.Prefix + ".capture_failed", $"ending the raw capture failed: {ex.GetType().Name}: {ex.Message}", WorkIqTraceLevel.Error, null, taskId: run.Tracker.TaskId, contextId: run.Tracker.ContextId);
            Diagnostics.Issue(
                new WorkIqIssue(default, WorkIqTraceStage.Capture, WorkIqIssueCategory.Capture, run.Prefix + ".capture_failed", $"the raw capture of the stream could not be finalised: {ex.GetType().Name}: {ex.Message}",
                    "The capture sink failed while closing (a full disk, an unwritable RawCaptureDirectory, or a bound exceeded). The frames were still delivered; the evidence carries no raw capture for this stream.")
                {
                    TaskId = run.Tracker.TaskId,
                    ContextId = run.Tracker.ContextId,
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

    /// <summary>The state of one stream call, shared by the steps of <see cref="RunStreamAsync"/>.</summary>
    private sealed class StreamRun
    {
        public StreamRun(WorkIqOperation operation, string? expectedTaskId, string? expectedContextId, int epoch, long started, RawCaptureSink sink)
        {
            Operation = operation;
            IsSubscribe = operation == WorkIqOperation.Subscribe;
            Stage = IsSubscribe ? WorkIqTraceStage.Subscribe : WorkIqTraceStage.Stream;
            Prefix = IsSubscribe ? "subscribe" : "stream";
            Tracker = new StreamIdTracker(expectedTaskId, expectedContextId);
            Epoch = epoch;
            Started = started;
            Sink = sink;
        }

        public WorkIqOperation Operation { get; }

        public bool IsSubscribe { get; }

        public WorkIqTraceStage Stage { get; }

        public string Prefix { get; }

        public StreamIdTracker Tracker { get; }

        public int Epoch { get; }

        public long Started { get; }

        public RawCaptureSink Sink { get; }

        public long Sequence { get; set; }

        public int Malformed { get; set; }

        public RawCaptureRef? Capture { get; set; }

        public bool CaptureEnded { get; set; }

        public Exception? TerminalException { get; set; }
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
