// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;

namespace AgentEval.Indexing;

// Preserve index refusal codes while sharing the bounded owned-copy implementation.
internal sealed class AefReadSnapshot(AefOwnedSnapshot snapshot) : IDisposable
{
    internal string FullName => snapshot.FullName;
    internal long Bytes => snapshot.Bytes;
    internal static AefReadSnapshot Capture(string directory, AefStagingOptions limits, CancellationToken cancellationToken)
    {
        try { return new(AefOwnedSnapshot.Capture(directory, limits, cancellationToken)); }
        catch (AefSnapshotException error) { throw new EvidenceIndexException(error.Code); }
    }
    public void Dispose() => snapshot.Dispose();
}
