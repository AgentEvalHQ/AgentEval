// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Capture;

/// <summary>The kind of one recorded exchange entry.</summary>
internal enum RawCaptureRecordKind
{
    /// <summary>An outgoing request: method, query-stripped URI, redacted headers and body.</summary>
    Request,

    /// <summary>A buffered (non-streamed) response: status, redacted headers and body.</summary>
    Response,

    /// <summary>A <c>text/event-stream</c> response, recorded byte for byte as the consumer read it.</summary>
    Stream,
}

/// <summary>
/// One recorded exchange entry, as a snapshot. <see cref="Body"/> is the redacted, bounded body only under
/// <see cref="EvidenceContentPolicy.FullEvidence"/>; under every other policy it is null and only the counts and
/// <see cref="Sha256"/> remain. A request and its response share a <see cref="Sequence"/>.
/// </summary>
/// <param name="Sequence">The exchange sequence (1-based; a request and its response share it).</param>
/// <param name="Kind">What the entry is.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The request URI with any query string replaced by a marker.</param>
/// <param name="HttpStatus">The response status (response and stream entries).</param>
/// <param name="Headers">The redacted request or response headers (content headers included).</param>
/// <param name="ContentType">The declared content type, when any.</param>
/// <param name="RecordedAt">When the entry was opened (diagnostics clock).</param>
/// <param name="BodyBytesObserved">How many body bytes passed through, bound or not.</param>
/// <param name="BodyBytesRetained">How many bytes were hashed (after the bound and redaction).</param>
/// <param name="Body">The retained bytes (a copy), only under <see cref="EvidenceContentPolicy.FullEvidence"/>.</param>
/// <param name="Truncated">Whether the byte bound cut this body.</param>
/// <param name="ReachedEnd">Stream entries only: whether the consumer read to end of stream (false = stopped early or failed); null for buffered entries.</param>
/// <param name="FileName">The file written under the capture directory, once <see cref="RawCaptureSink.Complete"/> wrote it.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the retained bytes; null when no byte was retained.</param>
internal sealed record RawCaptureRecord(
    int Sequence,
    RawCaptureRecordKind Kind,
    string? Method,
    string? Uri,
    int? HttpStatus,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    DateTimeOffset RecordedAt,
    long BodyBytesObserved,
    long BodyBytesRetained,
    byte[]? Body,
    bool Truncated,
    bool? ReachedEnd,
    string? FileName,
    string? Sha256);

/// <summary>One recorded SSE frame: its envelope and metadata, and under <see cref="EvidenceContentPolicy.FullEvidence"/> its redacted data.</summary>
/// <param name="ClientSequence">The frame's client sequence.</param>
/// <param name="ConnectionEpoch">The connection that delivered it.</param>
/// <param name="ReceivedAt">Local receipt time.</param>
/// <param name="SseId">The SSE <c>id</c> field, when any.</param>
/// <param name="SseEvent">The SSE <c>event</c> field, when any.</param>
/// <param name="PayloadCase">The typed payload case when the frame parsed (<c>Task</c>, <c>Message</c>, <c>StatusUpdate</c>, <c>ArtifactUpdate</c>).</param>
/// <param name="ByteLength">UTF-8 length of the raw <c>data</c> payload; null when the frame carried none.</param>
/// <param name="FailureCategory">The frame-level failure category, when the frame could not be used.</param>
/// <param name="FailureMessage">The frame-level failure's safe message (redacted), when any.</param>
/// <param name="RpcErrorCode">The JSON-RPC error code of a frame-level error, when any.</param>
/// <param name="DataJson">The redacted raw <c>data</c> payload, only under <see cref="EvidenceContentPolicy.FullEvidence"/>.</param>
internal sealed record RawCaptureFrameRecord(
    long ClientSequence,
    int ConnectionEpoch,
    DateTimeOffset ReceivedAt,
    string? SseId,
    string? SseEvent,
    string? PayloadCase,
    int? ByteLength,
    string? FailureCategory,
    string? FailureMessage,
    int? RpcErrorCode,
    string? DataJson);

/// <summary>
/// The per-turn store the <see cref="RawCaptureHandler"/> tees into: request body, response body and SSE frames,
/// bounded by <see cref="MaxBytes"/> / <see cref="MaxFrames"/> (exceeding either marks the capture
/// <see cref="CaptureState.Truncated"/> and registers a <see cref="WorkIqIssueCategory.Capture"/> issue — never
/// silently), with <c>Authorization</c>, <c>Cookie</c>/<c>Set-Cookie</c> and query strings redacted and every body
/// passed through <see cref="WorkIqRedaction"/> before retention. Under <see cref="EvidenceContentPolicy.FullEvidence"/>
/// the bytes are kept in memory and, when <see cref="Directory"/> is set, written to
/// <c>&lt;Directory&gt;/&lt;captureId&gt;/request-&lt;n&gt;.json</c>, <c>response-&lt;n&gt;.json</c> or
/// <c>response-&lt;n&gt;.sse</c>, <c>frames.jsonl</c> and a <c>manifest.json</c> with the SHA-256 of every file and
/// the redacted headers; under <see cref="EvidenceContentPolicy.SelectedContent"/> and
/// <see cref="EvidenceContentPolicy.MetadataOnly"/> no body is kept (the reference's state is
/// <see cref="CaptureState.NotRetained"/>) but the counts and the SHA-256 of what passed through are. The byte bound
/// is applied before redaction (bounded memory), so a redaction marker can move the retained length by a few bytes;
/// the hash always describes the retained (redacted) bytes, never the wire bytes. Thread-safe.
/// </summary>
internal sealed class RawCaptureSink
{
    /// <summary>The versioned capture/redaction policy id stamped into every <see cref="RawCaptureRef.PolicyId"/>.</summary>
    public const string PolicyId = "workiq-raw-capture/v1";

    /// <summary>The manifest file name inside the capture directory.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The frames file name inside the capture directory.</summary>
    public const string FramesFileName = "frames.jsonl";

    /// <summary>What replaces a query string in a recorded URI.</summary>
    public const string RedactedQueryMarker = "?" + WorkIqRedaction.RedactedMarker;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly JsonSerializerOptions ManifestOptions = CreateManifestOptions();
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly char[] TailDelimiters = [' ', '\t', '\r', '\n', '"', '\'', ',', ';', '<', '>', '&'];

    private readonly object _gate = new();
    private readonly WorkIqDiagnostics _diagnostics;
    private readonly List<Entry> _entries = [];
    private readonly List<RawCaptureFrameRecord> _frames = [];
    private readonly List<KeyValuePair<HttpRequestMessage, int>> _pendingRequests = [];
    private readonly IncrementalHash _overallHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _nextSequence = 1;
    private long _bytesObserved;
    private long _budgetUsed;
    private long _bytesRetained;
    private int _frameCount;
    private bool _truncated;
    private bool _interrupted;
    private bool _truncationReported;
    private bool _afterCompleteReported;
    private RawCaptureRef? _completed;

    /// <summary>Creates a sink for one turn.</summary>
    /// <param name="captureId">The capture id (unique inside the turn).</param>
    /// <param name="contentPolicy">What to retain.</param>
    /// <param name="maxBytes">Upper bound of retained bytes.</param>
    /// <param name="maxFrames">Upper bound of retained SSE frames.</param>
    /// <param name="directory">Where <see cref="EvidenceContentPolicy.FullEvidence"/> writes; null keeps everything in memory/hashes only.</param>
    /// <param name="diagnostics">The diagnostics capture events (<c>capture.*</c>) and issues are emitted through; null means <see cref="WorkIqDiagnostics.None"/>.</param>
    public RawCaptureSink(string captureId, EvidenceContentPolicy contentPolicy, long maxBytes, int maxFrames, DirectoryInfo? directory, WorkIqDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        CaptureId = captureId;
        ContentPolicy = contentPolicy;
        MaxBytes = maxBytes;
        MaxFrames = maxFrames;
        Directory = directory;
        _diagnostics = diagnostics ?? WorkIqDiagnostics.None;
    }

    /// <summary>The capture id.</summary>
    public string CaptureId { get; }

    /// <summary>What the sink retains.</summary>
    public EvidenceContentPolicy ContentPolicy { get; }

    /// <summary>Upper bound of retained bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>Upper bound of retained SSE frames.</summary>
    public int MaxFrames { get; }

    /// <summary>Where full-evidence captures are written, when configured.</summary>
    public DirectoryInfo? Directory { get; }

    /// <summary>Whether bodies are kept (<see cref="EvidenceContentPolicy.FullEvidence"/>); otherwise only counts and hashes survive.</summary>
    public bool RetainsBodies => ContentPolicy == EvidenceContentPolicy.FullEvidence;

    /// <summary>The directory this capture's files go to (<c>&lt;Directory&gt;/&lt;captureId&gt;</c>, id made file-system safe); null without a <see cref="Directory"/>.</summary>
    public string? CaptureDirectory => Directory is null ? null : Path.Combine(Directory.FullName, ToSafeFileName(CaptureId));

    /// <summary>Whether a bound cut the capture.</summary>
    public bool IsTruncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>Whether the capture stopped before the operation ended (exception, early stop, or still open at <see cref="Complete"/>).</summary>
    public bool IsInterrupted
    {
        get
        {
            lock (_gate)
            {
                return _interrupted;
            }
        }
    }

    /// <summary>Whether <see cref="Complete"/> ran.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed is not null;
            }
        }
    }

    /// <summary>The reference returned by <see cref="Complete"/>; null before it ran.</summary>
    public RawCaptureRef? Reference
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    /// <summary>How many body bytes passed through so far, bound or not.</summary>
    public long BytesObserved
    {
        get
        {
            lock (_gate)
            {
                return _bytesObserved;
            }
        }
    }

    /// <summary>How many bytes were retained for hashing so far (an open stream counts its current buffer).</summary>
    public long BytesRetained
    {
        get
        {
            lock (_gate)
            {
                return _completed is null ? _entries.Sum(e => e.RetainedLength) : _bytesRetained;
            }
        }
    }

    /// <summary>How many frames were recorded (counted even past <see cref="MaxFrames"/>).</summary>
    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frameCount;
            }
        }
    }

    /// <summary>How many frames were retained (at most <see cref="MaxFrames"/>).</summary>
    public int FramesRetained
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// The current state: <see cref="CaptureState.NotRetained"/> whenever bodies are not retained by policy, else
    /// <see cref="CaptureState.Interrupted"/> (wins over truncation: the operation did not end), else
    /// <see cref="CaptureState.Truncated"/>, else <see cref="CaptureState.Complete"/>.
    /// </summary>
    public CaptureState State
    {
        get
        {
            lock (_gate)
            {
                return ComputeState();
            }
        }
    }

    /// <summary>The exchange entries recorded so far (a snapshot; bodies copied).</summary>
    public IReadOnlyList<RawCaptureRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(e => e.ToRecord()).ToArray();
            }
        }
    }

    /// <summary>The frames retained so far (a snapshot).</summary>
    public IReadOnlyList<RawCaptureFrameRecord> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    /// <summary>Records the (redacted) request line, headers and body.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="body">The request body bytes.</param>
    public void RecordRequest(HttpRequestMessage request, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (RefuseAfterComplete("request"))
            {
                return;
            }

            var entry = new Entry(_nextSequence++, RawCaptureRecordKind.Request, _diagnostics.Clock.GetUtcNow())
            {
                Method = request.Method.Method,
                Uri = RedactUri(request.RequestUri),
                Headers = WorkIqRedaction.RedactHeaders(request),
                ContentType = request.Content?.Headers.ContentType?.ToString(),
            };
            _pendingRequests.Add(new KeyValuePair<HttpRequestMessage, int>(request, entry.Sequence));
            RetainBody(entry, body.Span);
            _entries.Add(entry);
        }
    }

    /// <summary>Records the (redacted) response status, headers and a non-streamed body.</summary>
    /// <param name="response">The response.</param>
    /// <param name="body">The response body bytes.</param>
    public void RecordResponse(HttpResponseMessage response, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (RefuseAfterComplete("response"))
            {
                return;
            }

            var entry = NewResponseEntry(response, RawCaptureRecordKind.Response);
            RetainBody(entry, body.Span);
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Opens a stream entry for a <c>text/event-stream</c> response (status and redacted headers recorded now) and
    /// returns the recorder the tee stream feeds byte for byte; <see cref="StreamRecorder.End"/> closes it. After
    /// <see cref="Complete"/> the recorder is detached and records nothing.
    /// </summary>
    /// <param name="response">The streaming response.</param>
    public StreamRecorder BeginStreamResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            var entry = NewResponseEntry(response, RawCaptureRecordKind.Stream);
            entry.StreamOpen = true;
            entry.RawBuffer = new MemoryStream();
            if (RefuseAfterComplete("streaming response"))
            {
                entry.Detached = true;
            }
            else
            {
                _entries.Add(entry);
            }

            return new StreamRecorder(this, entry);
        }
    }

    /// <summary>Records one SSE frame as received (metadata always; the data payload only under <see cref="EvidenceContentPolicy.FullEvidence"/>).</summary>
    /// <param name="frame">The frame.</param>
    public void RecordFrame(WorkIqStreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (RefuseAfterComplete("frame"))
            {
                return;
            }

            _frameCount++;
            if (_frames.Count >= MaxFrames)
            {
                Truncate($"MaxFrames ({MaxFrames}) reached at frame #{frame.ClientSequence}: the frame is counted, not retained");
                return;
            }

            var dataJson = frame.DataJson;
            _frames.Add(new RawCaptureFrameRecord(
                frame.ClientSequence,
                frame.ConnectionEpoch,
                frame.ReceivedAt,
                frame.SseId,
                frame.SseEvent,
                frame.Event?.PayloadCase.ToString(),
                dataJson is null ? null : Utf8.GetByteCount(dataJson),
                frame.Failure?.Category.ToString(),
                frame.Failure is null ? null : WorkIqRedaction.Redact(frame.Failure.SafeMessage),
                frame.Failure?.RpcErrorCode,
                RetainsBodies && dataJson is not null ? WorkIqRedaction.Redact(dataJson) : null));
        }
    }

    /// <summary>Marks the capture interrupted (the connection or the local wait ended before the operation did); traced and registered once.</summary>
    public void MarkInterrupted()
    {
        lock (_gate)
        {
            MarkInterruptedCore("the exchange ended before the operation did");
        }
    }

    /// <summary>
    /// Finalises the capture: closes any stream still open (as interrupted), computes the overall SHA-256 of the
    /// retained bytes, writes the files under <see cref="EvidenceContentPolicy.FullEvidence"/> when a
    /// <see cref="Directory"/> is set (a write failure is traced and registered; the reference then has no location),
    /// drops the bodies under the other policies, and returns the reference. Idempotent: later calls return the
    /// same reference. <see cref="RawCaptureRef.Sha256"/> and <see cref="RawCaptureRef.ByteCount"/> describe the
    /// bytes that went through the hash (null when none did), under every policy.
    /// </summary>
    public RawCaptureRef Complete()
    {
        lock (_gate)
        {
            if (_completed is not null)
            {
                return _completed;
            }

            foreach (var entry in _entries)
            {
                if (entry.StreamOpen)
                {
                    EndStreamCore(entry, reachedEnd: false, why: "Complete() was called while the stream was still open");
                }
            }

            _bytesRetained = _entries.Sum(e => e.RetainedLength);
            var sha256 = _bytesRetained > 0 ? ToHex(_overallHash.GetHashAndReset()) : null;
            _overallHash.Dispose();

            var state = ComputeState();
            var location = RetainsBodies && Directory is not null ? TryWriteFiles(state, sha256) : null;
            if (!RetainsBodies)
            {
                foreach (var entry in _entries)
                {
                    entry.Body = null;
                }
            }

            _completed = new RawCaptureRef(CaptureId, state, PolicyId, location, sha256, _bytesRetained > 0 ? _bytesRetained : null);
            _diagnostics.Trace(
                WorkIqTraceStage.Capture,
                "capture.completed",
                $"capture {CaptureId} completed: state={state}, bytes observed={_bytesObserved}, retained={_bytesRetained}, frames={_frameCount} (retained {_frames.Count}), policy={ContentPolicy}" +
                (location is null ? "" : $", written to {location}"));
            return _completed;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Internals (every method below runs under _gate unless stated)
    // ---------------------------------------------------------------------------------------------------------

    private void AppendStream(Entry entry, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (!entry.StreamOpen || entry.Detached)
            {
                return;
            }

            entry.ObservedLength += bytes.Length;
            _bytesObserved += bytes.Length;
            var budget = Math.Max(0, MaxBytes - _budgetUsed);
            var keep = (int)Math.Min(bytes.Length, budget);
            if (keep < bytes.Length)
            {
                entry.Truncated = true;
                Truncate($"MaxBytes ({MaxBytes}) reached while recording the stream of exchange #{entry.Sequence}: {bytes.Length - keep} byte(s) of this read dropped");
            }

            if (keep > 0)
            {
                entry.RawBuffer!.Write(bytes[..keep]);
                _budgetUsed += keep;
            }
        }
    }

    private void EndStream(Entry entry, bool reachedEnd)
    {
        lock (_gate)
        {
            EndStreamCore(entry, reachedEnd, reachedEnd ? "end of stream" : "the consumer stopped or the read failed before end of stream");
        }
    }

    private void EndStreamCore(Entry entry, bool reachedEnd, string why)
    {
        if (!entry.StreamOpen)
        {
            return;
        }

        entry.StreamOpen = false;
        entry.ReachedEnd = reachedEnd;
        var kept = entry.RawBuffer?.ToArray() ?? [];
        entry.RawBuffer?.Dispose();
        entry.RawBuffer = null;
        if (entry.Detached)
        {
            return;
        }

        FinalizeBody(entry, kept, entry.Truncated);
        if (!reachedEnd)
        {
            MarkInterruptedCore($"stream of exchange #{entry.Sequence}: {why}");
        }
    }

    private void RetainBody(Entry entry, ReadOnlySpan<byte> raw)
    {
        entry.ObservedLength = raw.Length;
        _bytesObserved += raw.Length;
        var budget = Math.Max(0, MaxBytes - _budgetUsed);
        var keep = (int)Math.Min(raw.Length, budget);
        var cut = keep < raw.Length;
        if (cut)
        {
            Truncate($"MaxBytes ({MaxBytes}) reached while recording {entry.Kind} #{entry.Sequence}: {raw.Length - keep} of {raw.Length} byte(s) dropped");
        }

        _budgetUsed += keep;
        FinalizeBody(entry, raw[..keep].ToArray(), cut);
    }

    private void FinalizeBody(Entry entry, byte[] kept, bool truncated)
    {
        entry.Truncated |= truncated;
        var retained = Redact(kept, truncated);
        entry.RetainedLength = retained.Length;
        if (retained.Length > 0)
        {
            entry.Sha256 = ToHex(SHA256.HashData(retained));
            _overallHash.AppendData(retained);
        }

        entry.Body = RetainsBodies ? retained : null;
    }

    private void Truncate(string reason)
    {
        _truncated = true;
        if (_truncationReported)
        {
            return;
        }

        _truncationReported = true;
        _diagnostics.Trace(WorkIqTraceStage.Capture, "capture.truncated", $"capture {CaptureId} truncated: {reason}", WorkIqTraceLevel.Warning);
        _diagnostics.Issue(
            WorkIqIssue.Create(
                WorkIqTraceStage.Capture,
                WorkIqIssueCategory.Capture,
                "capture.truncated",
                $"capture {CaptureId} truncated: {reason}",
                $"a raw capture bound was exceeded, so the retained material cannot reproduce the exchange (state Truncated — never a natural end of result); raise WorkIqOptions.MaxCaptureBytes ({MaxBytes}) / MaxCaptureFrames ({MaxFrames}) to retain more"),
            WorkIqTraceLevel.Warning);
    }

    private void MarkInterruptedCore(string reason)
    {
        if (_interrupted)
        {
            return;
        }

        _interrupted = true;
        _diagnostics.Trace(WorkIqTraceStage.Capture, "capture.interrupted", $"capture {CaptureId} interrupted: {reason}", WorkIqTraceLevel.Warning);
        _diagnostics.Issue(
            WorkIqIssue.Create(
                WorkIqTraceStage.Capture,
                WorkIqIssueCategory.Capture,
                "capture.interrupted",
                $"capture {CaptureId} interrupted: {reason}",
                "the capture of this exchange is incomplete because the connection, the consumer or the local wait stopped before the operation ended; the transport failure that caused it (if any) is registered separately with its own category"),
            WorkIqTraceLevel.Warning);
    }

    private bool RefuseAfterComplete(string what)
    {
        if (_completed is null)
        {
            return false;
        }

        if (!_afterCompleteReported)
        {
            _afterCompleteReported = true;
            _diagnostics.Trace(WorkIqTraceStage.Capture, "capture.after_complete", $"capture {CaptureId}: a {what} arrived after Complete() and was not recorded", WorkIqTraceLevel.Warning);
            _diagnostics.Issue(
                WorkIqIssue.Create(
                    WorkIqTraceStage.Capture,
                    WorkIqIssueCategory.Capture,
                    "capture.after_complete",
                    $"capture {CaptureId}: a {what} arrived after Complete() and was not recorded",
                    "the turn finalised its capture while an exchange was still in flight; the late exchange is missing from the capture (a provider sequencing bug — report it with the trace)"),
                WorkIqTraceLevel.Warning);
        }

        return true;
    }

    private Entry NewResponseEntry(HttpResponseMessage response, RawCaptureRecordKind kind)
    {
        var sequence = PairedSequence(response.RequestMessage) ?? _nextSequence++;
        return new Entry(sequence, kind, _diagnostics.Clock.GetUtcNow())
        {
            Method = response.RequestMessage?.Method.Method,
            Uri = RedactUri(response.RequestMessage?.RequestUri),
            HttpStatus = (int)response.StatusCode,
            Headers = WorkIqRedaction.RedactHeaders(response),
            ContentType = response.Content?.Headers.ContentType?.ToString(),
        };
    }

    private int? PairedSequence(HttpRequestMessage? request)
    {
        if (request is null)
        {
            return null;
        }

        for (var i = 0; i < _pendingRequests.Count; i++)
        {
            if (ReferenceEquals(_pendingRequests[i].Key, request))
            {
                var sequence = _pendingRequests[i].Value;
                _pendingRequests.RemoveAt(i);
                return sequence;
            }
        }

        return null;
    }

    private CaptureState ComputeState()
    {
        if (!RetainsBodies)
        {
            return CaptureState.NotRetained;
        }

        if (_interrupted)
        {
            return CaptureState.Interrupted;
        }

        return _truncated ? CaptureState.Truncated : CaptureState.Complete;
    }

    private string? TryWriteFiles(CaptureState state, string? sha256)
    {
        var directory = CaptureDirectory!;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var files = new List<ManifestFile>(_entries.Count);
            var bodyFiles = 0;
            foreach (var entry in _entries)
            {
                if (entry.Body is { Length: > 0 } body)
                {
                    entry.FileName = entry.Kind switch
                    {
                        RawCaptureRecordKind.Request => $"request-{entry.Sequence}.json",
                        RawCaptureRecordKind.Response => $"response-{entry.Sequence}.json",
                        _ => $"response-{entry.Sequence}.sse",
                    };
                    File.WriteAllBytes(Path.Combine(directory, entry.FileName), body);
                    bodyFiles++;
                }

                files.Add(entry.ToManifestFile());
            }

            ManifestFrames? frames = null;
            if (_frames.Count > 0)
            {
                var lines = new StringBuilder();
                foreach (var frame in _frames)
                {
                    lines.Append(JsonSerializer.Serialize(frame, WorkIqDiagnosticsJson.Options)).Append('\n');
                }

                var frameBytes = WorkIqDiagnosticsJson.Utf8NoBom.GetBytes(lines.ToString());
                File.WriteAllBytes(Path.Combine(directory, FramesFileName), frameBytes);
                frames = new ManifestFrames(FramesFileName, _frames.Count, ToHex(SHA256.HashData(frameBytes)));
            }

            var manifest = new Manifest(
                CaptureId,
                PolicyId,
                ContentPolicy,
                state,
                _truncated,
                _interrupted,
                MaxBytes,
                MaxFrames,
                _bytesObserved,
                _bytesRetained,
                _frameCount,
                _frames.Count,
                sha256,
                _diagnostics.Clock.GetUtcNow(),
                files,
                frames);
            File.WriteAllBytes(Path.Combine(directory, ManifestFileName), WorkIqDiagnosticsJson.Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest, ManifestOptions)));
            _diagnostics.Trace(
                WorkIqTraceStage.Capture,
                "capture.written",
                $"capture {CaptureId} written to {directory}: {bodyFiles} body file(s), {_frames.Count} frame(s), {ManifestFileName}");
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            foreach (var entry in _entries)
            {
                entry.FileName = null;
            }

            var message = $"capture {CaptureId} could not be written to {directory}: {ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}";
            _diagnostics.Trace(WorkIqTraceStage.Capture, "capture.write_failed", message, WorkIqTraceLevel.Error);
            _diagnostics.Issue(
                WorkIqIssue.Create(
                    WorkIqTraceStage.Capture,
                    WorkIqIssueCategory.Capture,
                    "capture.write_failed",
                    message,
                    "the raw capture directory is not writable (permissions, a missing drive, a path too long, or an invalid capture id); the bytes stay in memory for this run only — fix WorkIqOptions.RawCaptureDirectory"));
            return null;
        }
    }

    private static byte[] Redact(byte[] kept, bool truncated)
    {
        if (kept.Length == 0)
        {
            return kept;
        }

        var text = Utf8.GetString(kept);
        var redacted = WorkIqRedaction.Redact(text);
        if (truncated)
        {
            redacted = TrimTruncatedTail(redacted);
        }

        return string.Equals(redacted, text, StringComparison.Ordinal) ? kept : Utf8.GetBytes(redacted);
    }

    // A bound can cut inside a token; redaction only recognises whole tokens, so after a cut the trailing run
    // (what follows the last delimiter) is dropped when it looks like the start of a JWT or a bearer credential.
    private static string TrimTruncatedTail(string text)
    {
        var start = text.LastIndexOfAny(TailDelimiters) + 1;
        if (start >= text.Length)
        {
            return text;
        }

        var looksLikeJwt = text.IndexOf("eyJ", start, StringComparison.Ordinal) >= 0;
        var looksLikeBearer = text.AsSpan(0, start).TrimEnd().EndsWith("Bearer", StringComparison.OrdinalIgnoreCase);
        return looksLikeJwt || looksLikeBearer ? text[..start] : text;
    }

    private static string? RedactUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            return WorkIqRedaction.Redact(uri.OriginalString);
        }

        var left = uri.GetLeftPart(UriPartial.Path);
        return string.IsNullOrEmpty(uri.Query) ? left : left + RedactedQueryMarker;
    }

    private static string ToSafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // '/', '\' and ':' are replaced on every platform so a capture id yields the same directory name everywhere.
        var chars = id.Trim().Select(c => c == '/' || c == '\\' || c == ':' || Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var name = new string(chars).Trim('.', ' ');
        return name.Length == 0 ? "capture" : name;
    }

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static JsonSerializerOptions CreateManifestOptions()
    {
        var options = new JsonSerializerOptions(WorkIqDiagnosticsJson.Options) { WriteIndented = true };
#if NET9_0_OR_GREATER
        options.NewLine = "\n";
#endif
        return options;
    }

    /// <summary>The recorder a tee stream feeds; returned by <see cref="BeginStreamResponse"/>.</summary>
    public sealed class StreamRecorder
    {
        private readonly RawCaptureSink _owner;
        private readonly Entry _entry;

        internal StreamRecorder(RawCaptureSink owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        /// <summary>The exchange sequence of the stream entry.</summary>
        public int Sequence => _entry.Sequence;

        /// <summary>How many bytes were appended so far.</summary>
        public long BytesObserved
        {
            get
            {
                lock (_owner._gate)
                {
                    return _entry.ObservedLength;
                }
            }
        }

        /// <summary>Whether <see cref="End"/> has not run yet.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_owner._gate)
                {
                    return _entry.StreamOpen;
                }
            }
        }

        /// <summary>Appends bytes exactly as the consumer read them (bounded by the sink's byte budget).</summary>
        /// <param name="bytes">The bytes just read.</param>
        public void Append(ReadOnlySpan<byte> bytes) => _owner.AppendStream(_entry, bytes);

        /// <summary>Closes the stream entry: <paramref name="reachedEnd"/> false marks the capture interrupted. Idempotent.</summary>
        /// <param name="reachedEnd">Whether end of stream was read.</param>
        public void End(bool reachedEnd) => _owner.EndStream(_entry, reachedEnd);
    }

    internal sealed class Entry
    {
        public Entry(int sequence, RawCaptureRecordKind kind, DateTimeOffset recordedAt)
        {
            Sequence = sequence;
            Kind = kind;
            RecordedAt = recordedAt;
        }

        public int Sequence { get; }

        public RawCaptureRecordKind Kind { get; }

        public DateTimeOffset RecordedAt { get; }

        public string? Method { get; init; }

        public string? Uri { get; init; }

        public int? HttpStatus { get; init; }

        public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders;

        public string? ContentType { get; init; }

        public long ObservedLength { get; set; }

        public long RetainedLength { get; set; }

        public byte[]? Body { get; set; }

        public bool Truncated { get; set; }

        public bool? ReachedEnd { get; set; }

        public bool StreamOpen { get; set; }

        public bool Detached { get; set; }

        public MemoryStream? RawBuffer { get; set; }

        public string? FileName { get; set; }

        public string? Sha256 { get; set; }

        public RawCaptureRecord ToRecord() =>
            new(Sequence, Kind, Method, Uri, HttpStatus, Headers, ContentType, RecordedAt, ObservedLength, RetainedLength, Body?.ToArray(), Truncated, ReachedEnd, FileName, Sha256);

        public ManifestFile ToManifestFile() =>
            new(Sequence, Kind, FileName, Method, Uri, HttpStatus, ContentType, RecordedAt, ObservedLength, RetainedLength, Truncated, ReachedEnd, Sha256, Headers);
    }

    internal sealed record Manifest(
        string CaptureId,
        string PolicyId,
        EvidenceContentPolicy ContentPolicy,
        CaptureState State,
        bool Truncated,
        bool Interrupted,
        long MaxBytes,
        int MaxFrames,
        long BytesObserved,
        long BytesRetained,
        int FrameCount,
        int FramesRetained,
        string? Sha256,
        DateTimeOffset CompletedAt,
        IReadOnlyList<ManifestFile> Files,
        ManifestFrames? Frames);

    internal sealed record ManifestFile(
        int Sequence,
        RawCaptureRecordKind Kind,
        string? FileName,
        string? Method,
        string? Uri,
        int? HttpStatus,
        string? ContentType,
        DateTimeOffset RecordedAt,
        long BytesObserved,
        long BytesRetained,
        bool Truncated,
        bool? ReachedEnd,
        string? Sha256,
        IReadOnlyDictionary<string, string> Headers);

    internal sealed record ManifestFrames(string FileName, int Count, string Sha256);
}
