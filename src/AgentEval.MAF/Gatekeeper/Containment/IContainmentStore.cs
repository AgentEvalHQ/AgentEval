// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Tenant-scoped containment state. Hot-path reads are synchronous immutable snapshots with no disk/network I/O;
/// durable mutations are asynchronous. Implementations must never translate unavailable/corrupt state into
/// <see cref="ContainmentSnapshotState.NotContained"/>.
/// </summary>
public interface IContainmentStore : IDisposable
{
    /// <summary>
    /// Returns the current bounded in-memory snapshot. Active and Indeterminate snapshots require enforcement to
    /// block. This method must not perform disk or network I/O.
    /// </summary>
    ContainmentSnapshot GetCurrent(ContainmentTarget target);

    /// <summary>
    /// Idempotently contains a target. Concurrent calls for an already-active target converge to one durable
    /// record and return Applied/Unchanged rather than creating competing active records.
    /// </summary>
    ValueTask<ContainmentMutationResult> ContainAsync(
        ContainmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases one exact active record version using signed operator authority. Stale, invalid, expired, or
    /// replayed authorizations return Conflict and never release a newer record.
    /// </summary>
    ValueTask<ContainmentMutationResult> ReleaseAsync(
        ContainmentReleaseAuthorization authorization,
        CancellationToken cancellationToken = default);
}
