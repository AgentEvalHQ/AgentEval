// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using System.Runtime.CompilerServices;

namespace AgentEval.MissionControl.Hosting;

/// <summary>
/// Read-only delegating wrapper around any <see cref="IOutputStoreReader"/>. Closes the
/// DI-architecture leak in plan-08 portal-review finding B1.
/// </summary>
/// <remarks>
/// <para>
/// Pre-2026-05-24 <c>McHost.ConfigureServices</c> registered
/// <c>new FileSystemOutputStore(...)</c> typed as <see cref="IOutputStoreReader"/> — but
/// <c>FileSystemOutputStore</c> implements the full <c>IOutputStore</c> write surface. Any
/// caller that downcast the reader (whether by accident or via a future bug) would gain
/// write access against the read-only-portal promise (Mode A explicitly forbids writes).
/// </para>
/// <para>
/// This adapter implements ONLY <see cref="IOutputStoreReader"/>. A downcast to the writer
/// interface returns <c>null</c>, so the read-only contract holds at runtime, not just at
/// the static signature level. The reflection-based <c>ReaderOnlyArchitectureTests</c>
/// passes either way; this adapter closes the runtime instance gap.
/// </para>
/// <para>
/// All methods forward to the wrapped reader with zero per-call overhead beyond the
/// virtual dispatch. The adapter holds no state of its own.
/// </para>
/// </remarks>
internal sealed class ReadOnlyOutputStoreAdapter : IOutputStoreReader
{
    private readonly IOutputStoreReader _inner;

    public ReadOnlyOutputStoreAdapter(IOutputStoreReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public string? WorkspaceRoot => _inner.WorkspaceRoot;

    public bool IsAvailable => _inner.IsAvailable;

    public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default)
        => _inner.EnsureSolutionAsync(ct);

    public IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default)
        => _inner.ListSubjectsAsync(kind, ct);

    public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
        => _inner.GetRunManifestAsync(runId, ct);

    public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default)
        => _inner.GetRunSummaryAsync(runId, ct);

    public IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default)
        => _inner.GetScenarioResultsAsync(runId, ct);

    public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default)
        => _inner.GetTraceAsync(runId, ct);

    public IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default)
        => _inner.ListRunsAsync(subject, ct);

    public IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default)
        => _inner.GetRecentRunsAsync(count, ct);

    public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default)
        => _inner.LoadBaselineAsync(subject, ct);

    public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default)
        => _inner.CompareToBaselineAsync(runId, ct);

    public IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default)
        => _inner.GetHistoryAsync(subject, range, ct);

    public IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default)
        => _inner.ListComplianceEvidenceAsync(regulation, subject, ct);

    public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default)
        => _inner.GetComplianceEvidenceAsync(regulation, subject, timestamp, ct);

    // T3.6 (2026-05-25): forward the new red-team read methods.
    public IAsyncEnumerable<RedTeamCampaignManifest> ListRedTeamCampaignsAsync(CancellationToken ct = default)
        => _inner.ListRedTeamCampaignsAsync(ct);

    public Task<RedTeamCampaignManifest?> GetRedTeamCampaignAsync(string campaignId, CancellationToken ct = default)
        => _inner.GetRedTeamCampaignAsync(campaignId, ct);
}
