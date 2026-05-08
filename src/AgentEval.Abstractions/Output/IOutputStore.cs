// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Abstracts all read/write operations against an AgentEval output folder.</summary>
public interface IOutputStore
{
    // ─── Solution lifecycle ──────────────────────────────────────────────────

    /// <summary>Ensures the solution root exists and returns its info.</summary>
    Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default);

    // ─── Subject lifecycle ───────────────────────────────────────────────────

    /// <summary>Ensures the subject folder exists and returns its current info.</summary>
    Task<SubjectInfo> EnsureSubjectAsync(SubjectIdentity identity, CancellationToken ct = default);

    /// <summary>Enumerates known subjects, optionally filtered by kind.</summary>
    IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default);

    // ─── Run lifecycle ───────────────────────────────────────────────────────

    /// <summary>Starts a new run for the given subject and persists its manifest.</summary>
    Task<RunManifest> StartRunAsync(SubjectIdentity subject, RunContext context, CancellationToken ct = default);

    /// <summary>Writes a single scenario result into the run's results file.</summary>
    Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default);

    /// <summary>Finalises a run by persisting its summary and updating run indexes.</summary>
    Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default);

    /// <summary>Appends an agent trace to the run's trace artifact.</summary>
    Task AppendTraceAsync(string runId, AgentTrace trace, CancellationToken ct = default);

    // ─── Baselines ───────────────────────────────────────────────────────────

    /// <summary>Saves the given summary as the baseline for the specified subject.</summary>
    Task SaveBaselineAsync(SubjectIdentity subject, RunSummary summary, string? versionTag = null, CancellationToken ct = default);

    /// <summary>Loads the current baseline summary for the given subject, or null if none exists.</summary>
    Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default);

    /// <summary>Compares the run identified by <paramref name="runId"/> against its subject's baseline.</summary>
    Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default);

    // ─── History ─────────────────────────────────────────────────────────────

    /// <summary>Appends a history entry to the subject's <c>history.jsonl</c> file.</summary>
    Task AppendHistoryEntryAsync(SubjectIdentity subject, HistoryEntry entry, CancellationToken ct = default);

    /// <summary>Streams history entries for the given subject, optionally filtered by date range.</summary>
    IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default);

    // ─── Compliance (v1) ─────────────────────────────────────────────────────

    /// <summary>Persists a compliance evidence document for a regulation and subject.</summary>
    Task SaveComplianceEvidenceAsync(string regulation, SubjectIdentity subject, ComplianceEvidence evidence, CancellationToken ct = default);

    /// <summary>Enumerates compliance evidence pointers, optionally filtered by regulation or subject.</summary>
    IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default);

    /// <summary>Retrieves a specific compliance evidence document by regulation, subject, and timestamp.</summary>
    Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default);

    // ─── Red-team (v1) ───────────────────────────────────────────────────────

    /// <summary>Starts a red-team campaign and persists its manifest.</summary>
    Task<RedTeamCampaignManifest> StartRedTeamCampaignAsync(RedTeamCampaignContext context, CancellationToken ct = default);

    /// <summary>Completes a red-team campaign by recording findings in its manifest.</summary>
    Task CompleteRedTeamCampaignAsync(string campaignId, RedTeamFindings findings, CancellationToken ct = default);

    // ─── Cross-cutting ────────────────────────────────────────────────────────

    /// <summary>Returns the most recent run pointers across all subjects, up to <paramref name="count"/>.</summary>
    IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default);

    // ─── Run retrieval ────────────────────────────────────────────────────────

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

    // ─── Workspace ────────────────────────────────────────────────────────────

    /// <summary>The resolved workspace root path, or null when the store is unavailable.</summary>
    string? WorkspaceRoot { get; }

    /// <summary>Indicates whether the store is ready to accept operations.</summary>
    bool IsAvailable { get; }
}

/// <summary>Context provided when starting a new evaluation run.</summary>
public sealed record RunContext(
    string EvalProject,
    string EvalProjectPath,
    string Harness,
    int? Seed,
    string? ParentInvocationId,
    string Kind);

/// <summary>Inclusive date range used when filtering history entries.</summary>
public sealed record DateRange(DateTimeOffset From, DateTimeOffset To);

/// <summary>Persistent solution-level information returned by <see cref="IOutputStore.EnsureSolutionAsync"/>.</summary>
public sealed record SolutionInfo(Guid Id, string Name, string Path);

/// <summary>Context for starting a red-team campaign.</summary>
public sealed record RedTeamCampaignContext(string Name, IReadOnlyList<SubjectIdentity> Targets, string Mode);

/// <summary>Findings recorded when completing a red-team campaign.</summary>
public sealed record RedTeamFindings(IReadOnlyList<object> Items);

/// <summary>Result of a single scenario evaluation within a run.</summary>
public sealed record ScenarioResult(
    string Id,
    string Name,
    string Input,
    string Output,
    bool Passed,
    double Score,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<AssertionResult> Assertions,
    TimeSpan Duration,
    double EstimatedCost);

/// <summary>Result of a single assertion within a scenario.</summary>
public sealed record AssertionResult(string Assertion, bool Passed, string? Message);

/// <summary>Full agent execution trace for a run.</summary>
public sealed record AgentTrace(string RunId, string ScenarioId, IReadOnlyList<TraceEvent> Events);

/// <summary>A single event within an agent execution trace.</summary>
public sealed record TraceEvent(DateTimeOffset Timestamp, string Kind, string? Name, string? Payload);
