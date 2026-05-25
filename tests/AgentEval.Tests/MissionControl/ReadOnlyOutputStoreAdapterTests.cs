// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.MissionControl.Hosting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Pins the plan-08 portal-review B1 fix (2026-05-24): the read-only adapter wraps any
/// <see cref="IOutputStoreReader"/> (including a concrete <c>FileSystemOutputStore</c>
/// that implements the full <c>IOutputStore</c> write surface) and the resulting instance
/// is NOT castable to <c>IOutputStore</c> at runtime — closing the DI-layer leak that
/// the reflection-only <c>ReaderOnlyArchitectureTests</c> cannot catch.
/// </summary>
public class ReadOnlyOutputStoreAdapterTests
{
    [Fact]
    public void Adapter_IsNotCastable_ToIOutputStore()
    {
        // Arrange — wrap a fake reader (no need for a real FileSystemOutputStore;
        // the point is to assert the runtime shape of the adapter, not the inner instance).
        var inner = new StubReader();

        // Act
        IOutputStoreReader adapter = new ReadOnlyOutputStoreAdapter(inner);

        // Assert
        Assert.False(adapter is IOutputStore,
            "ReadOnlyOutputStoreAdapter must not satisfy `is IOutputStore` — that's the DI-leak this adapter exists to close.");
        // Explicit cast-attempt also returns null (extra-belt on top of the type check).
        Assert.Null(adapter as IOutputStore);
    }

    [Fact]
    public void Adapter_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReadOnlyOutputStoreAdapter(null!));
    }

    [Fact]
    public void Adapter_ForwardsWorkspaceRoot()
    {
        var inner = new StubReader { WorkspaceRoot = "/test/root" };
        IOutputStoreReader adapter = new ReadOnlyOutputStoreAdapter(inner);

        Assert.Equal("/test/root", adapter.WorkspaceRoot);
        Assert.True(adapter.IsAvailable);
    }

    // Minimal stub that satisfies IOutputStoreReader without implementing IOutputStore.
    // Using a fake here lets the test stay deterministic without filesystem touch.
    private sealed class StubReader : IOutputStoreReader
    {
        public string? WorkspaceRoot { get; set; }
        public bool IsAvailable { get; set; } = true;

        public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<SubjectInfo>();

        public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<RunManifest?>(null);

        public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<RunSummary?>(null);

        public IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default)
            => AsyncEnumerable.Empty<ScenarioResult>();

        public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<AgentTrace?>(null);

        public IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default)
            => AsyncEnumerable.Empty<RunManifest>();

        public IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default)
            => AsyncEnumerable.Empty<RunPointer>();

        public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default)
            => Task.FromResult<RunSummary?>(null);

        public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<HistoryEntry>();

        public IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<ComplianceEvidencePointer>();

        public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default)
            => Task.FromResult<ComplianceEvidence?>(null);

        // T3.6: new red-team campaign read methods.
        public IAsyncEnumerable<RedTeamCampaignManifest> ListRedTeamCampaignsAsync(CancellationToken ct = default)
            => AsyncEnumerable.Empty<RedTeamCampaignManifest>();

        public Task<RedTeamCampaignManifest?> GetRedTeamCampaignAsync(string campaignId, CancellationToken ct = default)
            => Task.FromResult<RedTeamCampaignManifest?>(null);

        // Plan-13 T4.1b item 11: deterministic path projection.
        public string ResolveRunDirectory(SubjectIdentity subject, string runId)
            => $"stub://{subject.Kind}/{subject.Name}/runs/{runId}";
    }
}

#endif
