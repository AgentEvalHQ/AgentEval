// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// Reduces task snapshots, <c>statusUpdate</c> and <c>artifactUpdate</c> events into the current
/// <see cref="WorkIqTaskSnapshot"/>: artifacts keyed by <c>(taskId, artifactId)</c> with <c>append</c>
/// true/false/omitted, <c>lastChunk</c> closing one artifact only, non-text parts preserved, unknown fields
/// counted as schema warnings, identical chunks never de-duplicated. A subscription's first snapshot
/// <b>replaces</b> state; a terminal snapshot reconciles it. It never treats HTTP 200, EOF, <c>lastChunk</c> or
/// telemetry as completion: only an observed <see cref="WorkIqTaskState.Completed"/> state is. Owned by lane C (M13).
/// </summary>
internal sealed class WorkIqTaskReducer
{
    /// <summary>Loads (or, on a subscription/recovery snapshot, replaces) the state from a full task.</summary>
    /// <param name="task">The SDK task.</param>
    /// <param name="source">Which operation produced it.</param>
    /// <param name="frame">The frame or reply it arrived in (for capture/sequence provenance).</param>
    public void Load(AgentTask task, WorkIqSnapshotSource source, WorkIqStreamFrame? frame) =>
        throw new NotImplementedException("TODO(M13)");

    /// <summary>Applies one validated stream frame (status update, artifact update, task snapshot or direct message).</summary>
    /// <param name="frame">The frame.</param>
    public void Apply(WorkIqStreamFrame frame) =>
        throw new NotImplementedException("TODO(M13)");

    /// <summary>Marks the transport interrupted (EOF or drop without a terminal state); the state itself is unchanged.</summary>
    public void MarkInterrupted() =>
        throw new NotImplementedException("TODO(M13)");

    /// <summary>The current reduced snapshot; null before any task identity was established.</summary>
    public WorkIqTaskSnapshot? Snapshot() =>
        throw new NotImplementedException("TODO(M13)");

    /// <summary>The direct message reply when the service answered without a task; null otherwise.</summary>
    public WorkIqMessage? DirectMessage() =>
        throw new NotImplementedException("TODO(M13)");
}
