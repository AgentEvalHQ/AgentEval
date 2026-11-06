// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// What a caller hands to one <see cref="IWorkIqA2AClient"/> operation, and what the client hands back: the JSON-RPC
/// id to send, the capture subfolder, the case and turn the call belongs to, whether the caller registers the issue
/// of a failure itself, and the raw capture of a stream once it ended. One instance per call, never shared: the
/// client keeps no per-call state of its own.
/// </summary>
internal sealed class WorkIqCallContext
{
    private RawCaptureRef? _streamCapture;

    /// <summary>The JSON-RPC id a blocking call sends; null mints one. A stream ignores it: the SDK mints its own id.</summary>
    public string? RpcId { get; init; }

    /// <summary>
    /// The <c>&lt;runId&gt;/&lt;turnId&gt;</c>-style subfolder the call's captures are written under
    /// (<see cref="WorkIqOptions.RawCaptureDirectory"/>, <see cref="EvidenceContentPolicy.FullEvidence"/> only); null means
    /// <c>&lt;runId&gt;/no-turn</c>.
    /// </summary>
    public string? CaptureScope { get; init; }

    /// <summary>The case id stamped on an issue registered below the caller (a token failure).</summary>
    public string? CaseId { get; init; }

    /// <summary>The turn id stamped on an issue registered below the caller (a token failure).</summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// Whether the caller registers one issue for the operation's failure itself (a turn does, for its outcome). The
    /// client then traces the failure and registers no issue, so one failure never reaches the issue register twice.
    /// </summary>
    public bool CallerRegistersIssues { get; init; }

    /// <summary>The raw capture of a stream (<c>SendStreamingMessage</c> or <c>SubscribeToTask</c>), set by the client when the stream ended; null before.</summary>
    public RawCaptureRef? StreamCapture
    {
        get => Volatile.Read(ref _streamCapture);
        set => Volatile.Write(ref _streamCapture, value);
    }
}
