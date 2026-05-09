// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Read-only contract over an AgentEval output folder. Mission Control's local viewer
/// (Mode A) and workspace aggregator (Mode B) consume this interface so they cannot
/// accidentally write to the user's <c>.agenteval/</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IOutputStore"/> inherits from this interface and adds the write methods
/// (<c>StartRunAsync</c>, <c>WriteScenarioResultAsync</c>, <c>SaveComplianceEvidenceAsync</c>,
/// etc.). Code that only needs to read should depend on <see cref="IOutputStoreReader"/>
/// directly — that constraint is verified in plan-08 Phase 1 by a reflection-based test.
/// </para>
/// <para>
/// <b>Note on <see cref="EnsureSolutionAsync"/></b>: despite the historical "Ensure" name,
/// the shipped <c>FileSystemOutputStore</c> implementation throws when no
/// <c>solution.json</c> exists rather than creating one. It is therefore safe to expose on
/// the reader interface. <c>EnsureSubjectAsync</c> stays on the write-side <see cref="IOutputStore"/>
/// because its implementation does mutate <c>subject.json</c>.
/// </para>
/// </remarks>
public interface IOutputStoreReader
{
    // ─── Solution ────────────────────────────────────────────────────────────

    /// <summary>Returns the solution info, or throws if no <c>solution.json</c> is present.</summary>
    Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default);

    // ─── Subject discovery (read-only) ───────────────────────────────────────

    /// <summary>Enumerates known subjects, optionally filtered by kind.</summary>
    IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default);

    // ─── Run retrieval ───────────────────────────────────────────────────────

    /// <summary>Retrieves the manifest for the given run ID, or null if not found.</summary>
    Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default);

    /// <summary>Retrieves the summary for the given run ID, or null if not found.</summary>
    Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default);

    /// <summary>Streams all scenario results for the given run.</summary>
    IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default);

    /// <summary>Retrieves the agent trace for the given run, or null if not found.</summary>
    Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default);

    /// <summary>Enumerates all run manifests for the given subject.</summary>
    IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default);

    /// <summary>Returns the most recent run pointers across all subjects, up to <paramref name="count"/>.</summary>
    IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default);

    // ─── Baselines (read) ────────────────────────────────────────────────────

    /// <summary>Loads the current baseline summary for the given subject, or null if none exists.</summary>
    Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default);

    /// <summary>Compares the run identified by <paramref name="runId"/> against its subject's baseline.</summary>
    Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default);

    // ─── History (read) ──────────────────────────────────────────────────────

    /// <summary>Streams history entries for the given subject, optionally filtered by date range.</summary>
    IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default);

    // ─── Compliance evidence (read) ─────────────────────────────────────────

    /// <summary>Enumerates compliance evidence pointers, optionally filtered by regulation or subject.</summary>
    IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default);

    /// <summary>Retrieves a specific compliance evidence document by regulation, subject, and timestamp.</summary>
    Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default);

    // ─── Workspace ────────────────────────────────────────────────────────────

    /// <summary>The resolved workspace root path, or null when the store is unavailable.</summary>
    string? WorkspaceRoot { get; }

    /// <summary>Indicates whether the store is ready to accept operations.</summary>
    bool IsAvailable { get; }
}
