// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Capture;

/// <summary>
/// A <see cref="DelegatingHandler"/> that tees every request and response of the A2A client into a
/// <see cref="RawCaptureSink"/> without altering what the SDK sees: request bytes before dispatch, response bytes
/// (or, for <c>text/event-stream</c>, each frame as it streams) after. The sink is the one the request carries in
/// <see cref="SinkOption"/> (the client's blocking calls), else <see cref="CurrentSink"/> (the SDK's stream requests,
/// which the client cannot prepare). It sits <b>below</b> <c>WorkIqAuthHandler</c> in the pipeline, so the
/// <c>Authorization</c> header is already present and is redacted by the sink. A buffered response body is read once,
/// recorded and handed back as an equal in-memory content; a streaming body is wrapped in a tee that records exactly
/// the bytes the consumer reads (the SDK reads it incrementally, so it is never buffered whole) and marks the capture
/// interrupted when the consumer stops, the read fails or the request throws. With no sink the handler is a pure
/// pass-through.
/// </summary>
internal sealed class RawCaptureHandler : DelegatingHandler
{
    /// <summary>The media type whose body is teed rather than buffered.</summary>
    public const string EventStreamMediaType = "text/event-stream";

    /// <summary>
    /// The request option carrying the sink of that one request. A sink on the request wins over
    /// <see cref="CurrentSink"/>, so a call never records into the capture of another call on the same client.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<RawCaptureSink> SinkOption = new("AgentEval.Microsoft365.CaptureSink");

    /// <summary>The sink of the open stream, for requests that carry none (<see cref="SinkOption"/>); null means pass-through (nothing is captured).</summary>
    public RawCaptureSink? CurrentSink { get; set; }

    /// <summary>The diagnostics capture events (<c>capture.*</c>) are traced through; never null.</summary>
    public WorkIqDiagnostics Diagnostics { get; init; } = WorkIqDiagnostics.None;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sink = request.Options.TryGetValue(SinkOption, out var own) ? own : CurrentSink;
        if (sink is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var started = Diagnostics.Clock.GetTimestamp();
        var label = $"{request.Method} {DescribeUri(request.RequestUri)}";

        var requestBytes = ReadOnlyMemory<byte>.Empty;
        if (request.Content is not null)
        {
            // Buffers the content inside HttpContent, so the inner handler re-sends the same bytes.
            requestBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        sink.RecordRequest(request, requestBytes);
        Diagnostics.Trace(WorkIqTraceStage.Capture, "capture.request", $"capture {sink.CaptureId}: recorded {label} request, {requestBytes.Length} body byte(s)");

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interrupted(sink, label, ex, started);
            throw;
        }

        try
        {
            var elapsedMs = Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds;
            var status = (int)response.StatusCode;
            var requestId = response.Headers.TryGetValues(WorkIqAuthHandler.RequestIdHeader, out var values) ? values.FirstOrDefault() : null;

            if (IsEventStream(response))
            {
                var inner = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var recorder = sink.BeginStreamResponse(response);
                var original = response.Content;
                var tee = new CapturingReadStream(inner, recorder, original, Diagnostics, sink.CaptureId, label);
                response.Content = ReplaceContent(original, new StreamContent(tee));
                Diagnostics.Trace(
                    WorkIqTraceStage.Capture,
                    "capture.response",
                    $"capture {sink.CaptureId}: {label} answered HTTP {status} as {EventStreamMediaType}; the stream is recorded as the SDK reads it",
                    httpStatus: status,
                    requestId: requestId,
                    elapsedMs: elapsedMs);
            }
            else
            {
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                sink.RecordResponse(response, body);
                var original = response.Content;
                response.Content = ReplaceContent(original, new ByteArrayContent(body));
                original.Dispose();
                Diagnostics.Trace(
                    WorkIqTraceStage.Capture,
                    "capture.response",
                    $"capture {sink.CaptureId}: recorded {label} response HTTP {status}, {body.Length} body byte(s), {response.Content.Headers.ContentType?.ToString() ?? "no content type"}",
                    httpStatus: status,
                    requestId: requestId,
                    elapsedMs: elapsedMs);
            }
        }
        catch (Exception ex)
        {
            Interrupted(sink, label, ex, started);
            response.Dispose();
            throw;
        }

        return response;
    }

    private void Interrupted(RawCaptureSink sink, string label, Exception ex, long started)
    {
        Diagnostics.Trace(
            WorkIqTraceStage.Capture,
            "capture.failed",
            $"capture {sink.CaptureId}: {label} ended by {ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}",
            WorkIqTraceLevel.Warning,
            elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
        sink.MarkInterrupted();
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content?.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the content headers of <paramref name="original"/> onto <paramref name="replacement"/> and returns it (the caller disposes the original when it may).</summary>
    private static HttpContent ReplaceContent(HttpContent original, HttpContent replacement)
    {
        foreach (var header in original.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return replacement;
    }

    private static string DescribeUri(Uri? uri)
    {
        if (uri is null)
        {
            return "(no uri)";
        }

        return uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : WorkIqRedaction.Redact(uri.OriginalString);
    }

    /// <summary>
    /// A read-only stream over the response stream that feeds every byte the consumer reads to the sink's recorder,
    /// closes the recording at end of stream, and marks it interrupted when a read fails or the stream is disposed
    /// before the end. Disposing it disposes the inner stream and the original content.
    /// </summary>
    private sealed class CapturingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly RawCaptureSink.StreamRecorder _recorder;
        private readonly HttpContent _originalContent;
        private readonly WorkIqDiagnostics _diagnostics;
        private readonly string _captureId;
        private readonly string _label;
        private bool _ended;
        private bool _disposed;

        public CapturingReadStream(
            Stream inner,
            RawCaptureSink.StreamRecorder recorder,
            HttpContent originalContent,
            WorkIqDiagnostics diagnostics,
            string captureId,
            string label)
        {
            _inner = inner;
            _recorder = recorder;
            _originalContent = originalContent;
            _diagnostics = diagnostics;
            _captureId = captureId;
            _label = label;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override bool CanTimeout => _inner.CanTimeout;

        public override int ReadTimeout
        {
            get => _inner.ReadTimeout;
            set => _inner.ReadTimeout = value;
        }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read;
            try
            {
                read = _inner.Read(buffer);
            }
            catch (Exception ex)
            {
                Fail(ex);
                throw;
            }

            Observe(buffer, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            try
            {
                read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(ex);
                throw;
            }

            Observe(buffer.Span, read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    End(reachedEnd: false, "disposed before end of stream");
                    _inner.Dispose();
                    _originalContent.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void Observe(ReadOnlySpan<byte> buffer, int read)
        {
            if (read > 0)
            {
                _recorder.Append(buffer[..read]);
                return;
            }

            End(reachedEnd: true, "end of stream");
        }

        private void Fail(Exception ex) => End(reachedEnd: false, $"read failed: {ex.GetType().Name}");

        private void End(bool reachedEnd, string why)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            _recorder.End(reachedEnd);
            _diagnostics.Trace(
                WorkIqTraceStage.Capture,
                "capture.stream_end",
                $"capture {_captureId}: {_label} stream ended ({why}) after {_recorder.BytesObserved} byte(s)" + (reachedEnd ? "" : "; the capture is interrupted"),
                reachedEnd ? WorkIqTraceLevel.Info : WorkIqTraceLevel.Warning);
        }
    }
}
