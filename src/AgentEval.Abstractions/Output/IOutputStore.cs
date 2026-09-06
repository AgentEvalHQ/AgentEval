// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Abstracts all read/write operations against an AgentEval output folder.
/// </summary>
/// <remarks>
/// <para>
/// Inherits read methods from <see cref="IOutputStoreReader"/>. Mission Control's
/// local viewer (Mode A) and workspace aggregator (Mode B) consume only
/// <see cref="IOutputStoreReader"/> so they cannot accidentally write — the constraint is
/// verified in plan-08 Phase 1 by a reflection-based test.
/// </para>
/// <para>
/// <b>Convention 5B (canonical evidence sink, plan-13 T4.1b item 13)</b>:
/// every benchmark family that writes audit-grade evidence MUST persist it through
/// <see cref="IOutputStore"/> rather than directly to <see cref="System.IO.File"/>.
/// This is the single chokepoint that hashes the run, threads the result through
/// <c>ContentHasher</c>, and keeps Mission Control + <c>agenteval doctor</c>'s
/// audit-chain validators honest. Reference call-sites:
/// </para>
/// <list type="bullet">
///   <item><c>GDPRComplianceReporter.SaveReportAsync</c> (compliance evidence)</item>
///   <item><c>EuAiActComplianceReporter.SaveReportAsync</c> (compliance evidence)</item>
///   <item><c>OWASPComplianceReporter.SaveReportAsync</c> (red-team evidence)</item>
///   <item><c>MITREATLASReporter.SaveReportAsync</c> (red-team evidence)</item>
///   <item><c>BenchmarkSampleHelpers.WriteReportsViaStoreAsync</c> (sample wrapper)</item>
///   <item><c>FileSystemOutputStore.SaveComplianceEvidenceAsync</c> (canonical sink)</item>
/// </list>
/// <para>
/// Cross-references: ADR-017 Convention 5 (rendering + persistence symmetry),
/// plan-13 §T4.1b item 11 (path resolution via <see cref="IOutputStoreReader.ResolveRunDirectory"/>),
/// and the sample-side architecture note in
/// <c>samples/AgentEval.Samples/Benchmarks/README.md</c>.
/// </para>
/// </remarks>
public interface IOutputStore : IOutputStoreReader
{
    // ─── Subject lifecycle (write) ───────────────────────────────────────────

    /// <summary>Ensures the subject folder exists and returns its current info.</summary>
    Task<SubjectInfo> EnsureSubjectAsync(SubjectIdentity identity, CancellationToken ct = default);

    // ─── Run lifecycle (write) ───────────────────────────────────────────────

    /// <summary>Starts a new run for the given subject and persists its manifest.</summary>
    Task<RunManifest> StartRunAsync(SubjectIdentity subject, RunContext context, CancellationToken ct = default);

    /// <summary>Writes a single scenario result into the run's results file.</summary>
    Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default);

    /// <summary>Finalises a run by persisting its summary and updating run indexes.</summary>
    Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default);

    /// <summary>Appends an agent trace to the run's trace artifact.</summary>
    Task AppendTraceAsync(string runId, AgentTrace trace, CancellationToken ct = default);

    // ─── Baselines (write) ───────────────────────────────────────────────────

    /// <summary>Saves the given summary as the baseline for the specified subject.</summary>
    Task SaveBaselineAsync(SubjectIdentity subject, RunSummary summary, string? versionTag = null, CancellationToken ct = default);

    // ─── History (write) ─────────────────────────────────────────────────────

    /// <summary>Appends a history entry to the subject's <c>history.jsonl</c> file.</summary>
    Task AppendHistoryEntryAsync(SubjectIdentity subject, HistoryEntry entry, CancellationToken ct = default);

    // ─── Compliance (write) ──────────────────────────────────────────────────

    /// <summary>Persists a compliance evidence document for a regulation and subject.</summary>
    Task SaveComplianceEvidenceAsync(string regulation, SubjectIdentity subject, ComplianceEvidence evidence, CancellationToken ct = default);

    // ─── Red-team (write) ────────────────────────────────────────────────────

    /// <summary>Starts a red-team campaign and persists its manifest.</summary>
    Task<RedTeamCampaignManifest> StartRedTeamCampaignAsync(RedTeamCampaignContext context, CancellationToken ct = default);

    /// <summary>Completes a red-team campaign by recording findings in its manifest.</summary>
    Task CompleteRedTeamCampaignAsync(string campaignId, RedTeamFindings findings, CancellationToken ct = default);
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

/// <summary>Persistent solution-level information returned by <see cref="IOutputStoreReader.EnsureSolutionAsync"/>.</summary>
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
    double EstimatedCost)
{
    /// <summary>
    /// A stable digest of the stimulus in <see cref="Input"/>, or <see langword="null"/> when the
    /// producer did not supply one. See <see cref="StimulusHash"/> for the rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-031 S2.</b> The point is to be able to SHOW that two runs were given the same
    /// stimulus, rather than to assume it because two files sit in the same tree. It is a
    /// prerequisite for S5 (<c>agenteval compare</c>, which must refuse to emit deltas across runs
    /// that were not asked the same thing) — and V1's finding is that the comparability data belongs
    /// on the RUN, computable without any manifest, which is why it lives here.
    /// </para>
    /// <para>
    /// ⚠ <b>It is a NON-POSITIONAL member on purpose.</b> Adding an eleventh positional parameter
    /// would break every construction site in and outside this repository. As an init-only property
    /// defaulting to <see langword="null"/>, and with the store's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c>, a producer that does not set it writes
    /// <b>byte-identical</b> scenario files — so no stored content hash moves. Verified by the full
    /// suite, not asserted.
    /// </para>
    /// <para>
    /// ⚠ <b>Null is "nobody computed one", never "the inputs differ".</b> A consumer comparing two
    /// runs must treat a null on either side as NOT COMPARABLE and say so — reading it as "no
    /// difference" is the silent-<c>{}</c> shape ADR-030 §4.2 rejects, and it fails in the
    /// flattering direction.
    /// </para>
    /// </remarks>
    public string? StimulusHash { get; init; }

    /// <summary>
    /// The eval key, version, effective bar, chance floor and judge fingerprint that produced this
    /// scenario — <see langword="null"/> when the producer recorded none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-031 finding V1, and it is the PREREQUISITE for S5, not S5.</b> V1 lists six facts a
    /// <c>compare</c> needs to be a pure function of two run directories: the stimulus, the eval's
    /// key, its version, the effective bar, the floor and the judge fingerprint. S2 landed the
    /// stimulus above; this lands the other five. Measured before it existed: a real run directory
    /// carried <b>five of the six nowhere</b>, so a <c>compare</c> written to Phase 7.5's acceptance
    /// would have exited 13 on every pair of runs in this repository — a command with one reachable
    /// outcome. Recording the facts is a different item from consuming them, and this is that item.
    /// </para>
    /// <para>
    /// ⚠ <b>Non-positional and null-defaulting, for the same reason as
    /// <see cref="StimulusHash"/>.</b> A producer that does not set it writes a
    /// <b>byte-identical</b> scenario file under the store's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c>, so no stored content hash moves because this
    /// member exists. Asserted against a file the real <c>FileSystemOutputStore</c> wrote, not
    /// against a copy of its settings.
    /// </para>
    /// <para>
    /// ⚠ <b>Null is "nobody recorded any of it", never "the runs agree".</b> Same rule as
    /// <see cref="StimulusHash"/>: a consumer reading a null on either side must say NOT COMPARABLE.
    /// </para>
    /// </remarks>
    public ComparabilityFacts? Comparability { get; init; }
}

// AssertionResult moved to AssertionResult.cs (same namespace and assembly, so this is not an
// API change) when it grew a three-valued Outcome and factory methods — see AE-01.

/// <summary>Full agent execution trace for a run.</summary>
public sealed record AgentTrace(string RunId, string ScenarioId, IReadOnlyList<TraceEvent> Events);

/// <summary>A single event within an agent execution trace.</summary>
public sealed record TraceEvent(DateTimeOffset Timestamp, string Kind, string? Name, string? Payload);
