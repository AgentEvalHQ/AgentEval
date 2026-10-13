// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Observations;

namespace AgentEval.Indexing;

/// <summary>Evidence classes remain distinct throughout indexing.</summary>
public enum IndexedEvidenceKind { Aef, Legacy, Observation }
/// <summary>Reported integrity state, not signer trust, completeness or a pass/fail verdict.</summary>
public enum IndexedVerification { Unverified, Intact, Invalid }
/// <summary>Evaluated run fields projected from a stable source snapshot.</summary>
public sealed record IndexedRun(string RunId, string? RunHash, string? SubjectId, string? SuiteId,
    string? SuiteDefinitionDigest, string? StartedAt, string? Outcome, string Completeness);
/// <summary>Per-case/trial evaluated result.</summary>
public sealed record IndexedResult(string ResultId, string? CaseId, long? Trial, string? Outcome);
/// <summary>One metric occurrence; textual exceptional/exact values are separate from a finite numeric score.</summary>
public sealed record IndexedMetric(string ResultId, string MetricId, int Ordinal, double? Score,
    string? ScoreText, bool? Passed, string Provenance);
/// <summary>
/// Immutable adapter projection. ContentDigest is the digest of the authoritative snapshot consumed by
/// the adapter. This model does not itself verify source evidence; only a verifying adapter may claim Intact.
/// </summary>
public sealed record EvidenceIndexSource(string RootId, string RelativePath, string ContentDigest,
    IndexedEvidenceKind Kind, IndexedVerification Verification, IndexedRun? Run,
    ImmutableArray<IndexedResult> Results, ImmutableArray<IndexedMetric> Metrics, OtlpObservationRecord? Observation);
/// <summary>Finite per-source and whole-index mutation budgets.</summary>
public sealed class EvidenceIndexWriteLimits
{
    /// <summary>Default 100000 rows per source; range 1..1000000.</summary>
    public int MaxRowsPerSource { get; init; } = 100_000;
    /// <summary>Default 16 MiB aggregate UTF-8 projection text; range 1..64 MiB.</summary>
    public int MaxTextBytesPerSource { get; init; } = 16 << 20;
    /// <summary>Default 100000 retained sources; range 1..1000000.</summary>
    public int MaxSources { get; init; } = 100_000;
    /// <summary>Default 1000000 total source-owned rows; range 1..10000000.</summary>
    public int MaxTotalRows { get; init; } = 1_000_000;
}
/// <summary>Checkpoint of the indexed projection, not a source authenticity claim.</summary>
public sealed record EvidenceIndexCheckpoint(string SourceId, string Fingerprint, long IndexedGeneration, string Kind);
/// <summary>A root-relative source and its last indexed checkpoint, used for guarded reconciliation.</summary>
public sealed record EvidenceIndexSourceCheckpoint(string RelativePath, EvidenceIndexCheckpoint Checkpoint);
/// <summary>Committed mutation result. No-op operations retain the global generation.</summary>
public sealed record EvidenceIndexMutation(string SourceId, string Fingerprint, long Generation, bool Changed);
/// <summary>Atomic source replacement and guarded deletion in a rebuildable index.</summary>
public interface IEvidenceIndexWriter : IDisposable
{
    /// <summary>Atomically binds a bounded set of root IDs to ordinary source directories and formats; rebinding refuses.</summary>
    void BindRoots(IEnumerable<EvidenceIndexRoot> roots, CancellationToken cancellationToken = default);
    /// <summary>Replaces all derived rows and the source checkpoint atomically; identical projections are skipped.</summary>
    EvidenceIndexMutation Upsert(EvidenceIndexSource source, CancellationToken cancellationToken = default);
    /// <summary>Reads one source checkpoint by its stable root-relative identity.</summary>
    EvidenceIndexCheckpoint? Find(string rootId, string relativePath, CancellationToken cancellationToken = default);
    /// <summary>Lists a bounded root-scoped checkpoint inventory for reconciliation; not a public query pagination API.</summary>
    ImmutableArray<EvidenceIndexSourceCheckpoint> ListSources(string rootId, CancellationToken cancellationToken = default);
    /// <summary>Deletes only when the caller's checkpoint still matches. Never deletes source files.</summary>
    EvidenceIndexMutation Remove(string rootId, string relativePath, string expectedFingerprint, CancellationToken cancellationToken = default);
}
