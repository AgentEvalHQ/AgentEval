// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Capture;

/// <summary>
/// The per-turn store the <see cref="RawCaptureHandler"/> tees into: request body, response body and SSE frames,
/// bounded by <see cref="MaxBytes"/> / <see cref="MaxFrames"/> (exceeding either marks the capture
/// <see cref="CaptureState.Truncated"/>), with <c>Authorization</c>, <c>Set-Cookie</c> and query strings redacted
/// before retention. Under <see cref="EvidenceContentPolicy.FullEvidence"/> the bytes are written beneath
/// <see cref="Directory"/> (<c>&lt;runId&gt;/&lt;turnId&gt;/…</c>) with their SHA-256; under the other policies only
/// counts and hashes are kept. A <c>Bearer</c> substring never appears in a capture. Owned by lane B (M12).
/// </summary>
internal sealed class RawCaptureSink
{
    /// <summary>The versioned capture/redaction policy id stamped into every <see cref="RawCaptureRef.PolicyId"/>.</summary>
    public const string PolicyId = "workiq-raw-capture/v1";

    /// <summary>Creates a sink for one turn.</summary>
    /// <param name="captureId">The capture id (unique inside the turn).</param>
    /// <param name="contentPolicy">What to retain.</param>
    /// <param name="maxBytes">Upper bound of retained bytes.</param>
    /// <param name="maxFrames">Upper bound of retained SSE frames.</param>
    /// <param name="directory">Where <see cref="EvidenceContentPolicy.FullEvidence"/> writes; null keeps everything in memory/hashes only.</param>
    public RawCaptureSink(string captureId, EvidenceContentPolicy contentPolicy, long maxBytes, int maxFrames, DirectoryInfo? directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        CaptureId = captureId;
        ContentPolicy = contentPolicy;
        MaxBytes = maxBytes;
        MaxFrames = maxFrames;
        Directory = directory;
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

    /// <summary>Records the (redacted) request line, headers and body.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="body">The request body bytes.</param>
    public void RecordRequest(HttpRequestMessage request, ReadOnlyMemory<byte> body) =>
        throw new NotImplementedException("TODO(M12)");

    /// <summary>Records the (redacted) response status, headers and a non-streamed body.</summary>
    /// <param name="response">The response.</param>
    /// <param name="body">The response body bytes.</param>
    public void RecordResponse(HttpResponseMessage response, ReadOnlyMemory<byte> body) =>
        throw new NotImplementedException("TODO(M12)");

    /// <summary>Records one SSE frame as received.</summary>
    /// <param name="frame">The frame.</param>
    public void RecordFrame(WorkIqStreamFrame frame) =>
        throw new NotImplementedException("TODO(M12)");

    /// <summary>Marks the capture interrupted (the connection or the local wait ended before the operation did).</summary>
    public void MarkInterrupted() =>
        throw new NotImplementedException("TODO(M12)");

    /// <summary>Finalises the capture (writes files under <see cref="EvidenceContentPolicy.FullEvidence"/>) and returns its reference.</summary>
    public RawCaptureRef Complete() =>
        throw new NotImplementedException("TODO(M12)");
}
