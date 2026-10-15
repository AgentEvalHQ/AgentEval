// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Results.Observations;

/// <summary>Explicit, dedicated observation storage. Retention refuses new records at capacity; it never evicts evidence.</summary>
public sealed class OtlpObservationStoreOptions
{
    /// <summary>Absolute dedicated root, disjoint from viewer and AEF storage.</summary>
    public required string Root { get; init; }
    /// <summary>Stable receiver-controlled source namespace, not a request attribute.</summary>
    public required string Source { get; init; }
    /// <summary>Explicit content opt-in; default Off.</summary>
    public OtlpContentCapture Capture { get; init; }
    /// <summary>Maximum accepted records. Default 4096; range 1..100000.</summary>
    public int MaxRecords { get; init; } = 4096;
    /// <summary>Maximum serialized record bytes. Default 16 MiB; range 1..64 MiB.</summary>
    public int MaxRecordBytes { get; init; } = 16 << 20;
    /// <summary>Maximum accepted bytes. Default 512 MiB; range 1..64 GiB. One extra bounded scratch record may exist.</summary>
    public long MaxStoredBytes { get; init; } = 512L << 20;
}
/// <summary>Versioned persisted evidence. Content and grouping provenance survive subsequent mapping changes.</summary>
public sealed record OtlpObservationRecord(string Version, string Source, OtlpContentSnapshot Content,
    ImmutableArray<OtlpObservedSession> Sessions);
/// <summary>Acceptance after flushed-file publication, or a freshly verified exact retry.</summary>
public sealed record OtlpObservationReceipt(string Id, long Bytes, int ReportedSpans, bool AlreadyAccepted);
/// <summary>Bounded catalogue entry; a filename alone is not an integrity verification.</summary>
public sealed record OtlpObservationEntry(string Id, long Bytes);
/// <summary>Separate authoritative observation records, from which query indexes can be rebuilt.</summary>
public interface IOtlpObservationStore : IDisposable
{
    /// <summary>Applies content policy to decoder-validated input before writing; refuses concurrent operations rather than queueing.</summary>
    OtlpObservationReceipt Accept(OtlpTraceBatch batch, CancellationToken cancellationToken = default);
    /// <summary>Lists the bounded retained catalogue in ID order. Read verifies a selected record.</summary>
    ImmutableArray<OtlpObservationEntry> List(CancellationToken cancellationToken = default);
    /// <summary>Reads one bounded record and verifies its content digest before deserialization.</summary>
    OtlpObservationRecord Read(string id, CancellationToken cancellationToken = default);
}
