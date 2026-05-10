// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Runtime.CompilerServices;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Locks down the headline compliance feature: when a compliance evidence's
/// recorded <c>SourceRun.ManifestHash</c> no longer matches the source run's
/// current <see cref="RunManifest.ContentHash"/>, the audit chain is broken
/// and the matrix must report <c>AllChainsValid: false</c>. The SPA's
/// <c>&lt;ManifestHashBadge/&gt;</c> renders red on this signal — the whole
/// compliance feature stops being trustworthy without it.
/// </summary>
/// <remarks>
/// <para>
/// The InMemoryOutputStore + filesystem store both reject hash mismatches at
/// write-time, so we can't seed a tampered state via the public write path.
/// This test uses a minimal stub <see cref="IOutputStoreReader"/> that
/// directly returns a manifest with hash <c>H1</c> and an evidence pointing
/// to hash <c>H2</c> — simulating post-write corruption (an attacker editing
/// a manifest.json on disk after evidence was sealed).
/// </para>
/// </remarks>
public class AuditChainTamperingTests
{
    [Fact]
    public async Task ComplianceMatrix_TamperedManifestHash_FlipsAllChainsValidToFalse()
    {
        const string evidenceClaimedHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string actualManifestHash  = "TAMPERED_HASH_DOES_NOT_MATCH_THE_EVIDENCE_RECORDED_HASH";

        var (subject, manifest, evidence) =
            BuildFixture(manifestHash: actualManifestHash, evidenceHash: evidenceClaimedHash);

        var reader = new TamperedReader(subject, manifest, evidence);
        var service = new ComplianceMatrixService(reader);

        var matrix = await service.BuildMatrixAsync("test-reg", CancellationToken.None);

        Assert.False(
            matrix.AllChainsValid,
            "Audit chain must be flagged invalid when the manifest's ContentHash drifts from the evidence's recorded SourceRun.ManifestHash.");
        Assert.NotEmpty(matrix.Subjects);
        Assert.NotEmpty(matrix.Cells);
    }

    [Fact]
    public async Task ComplianceMatrix_HashesMatch_AllChainsValidIsTrue()
    {
        // Companion test: same shape but with hashes aligned. Locks down the
        // happy path so a future regression in the chain-check (e.g. inverted
        // condition) doesn't go silently unnoticed.
        const string sharedHash = "SHARED_HASH_VALID_AUDIT_CHAIN";
        var (subject, manifest, evidence) =
            BuildFixture(manifestHash: sharedHash, evidenceHash: sharedHash);

        var reader = new TamperedReader(subject, manifest, evidence);
        var service = new ComplianceMatrixService(reader);

        var matrix = await service.BuildMatrixAsync("test-reg", CancellationToken.None);

        Assert.True(matrix.AllChainsValid);
    }

    private static (SubjectIdentity Subject, RunManifest Manifest, ComplianceEvidence Evidence)
        BuildFixture(string manifestHash, string evidenceHash)
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var generatedAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

        var manifest = new RunManifest(
            SchemaVersion: "1.0",
            Solution: new SolutionRef(Guid.NewGuid(), "test", null),
            Subject: new SubjectRef(SubjectKind.Agent, "TestAgent", "1.0", null, null, null, null),
            Run: new RunRef(
                RunId: "test-run",
                Timestamp: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromSeconds(1),
                ScenarioCount: 1,
                Verdict: "PASS",
                Kind: "eval",
                EvalProject: null,
                EvalProjectPath: null,
                Harness: "xunit",
                Seed: 1,
                ParentInvocationId: null),
            Git: new GitRef("commit", "main", false, null),
            AgentEval: new AgentEvalRef("1.0", null),
            Environment: new EnvRef("machine", "os", "10.0", false, null, null),
            ContentHash: manifestHash);

        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "test-reg",
            Subject: subject,
            GeneratedAt: generatedAt,
            SourceRun: new SourceRunRef("test-run", evidenceHash),
            Controls: new[]
            {
                new EvidenceControl("ctrl-1", "Test control", "pass", 1.0,
                    Array.Empty<string>(), Notes: null),
            },
            Summary: new EvidenceSummary(1, 1, 0, 0, "pass"),
            Attestation: new Attestation("1.0", null, "stub", "stub"));

        return (subject, manifest, evidence);
    }

    /// <summary>
    /// Minimal <see cref="IOutputStoreReader"/> stub that returns one subject,
    /// one manifest, and one evidence pointer / record — enough for
    /// <see cref="ComplianceMatrixService.BuildMatrixAsync"/>'s read path.
    /// </summary>
    private sealed class TamperedReader : IOutputStoreReader
    {
        private readonly SubjectIdentity _subject;
        private readonly RunManifest _manifest;
        private readonly ComplianceEvidence _evidence;
        private readonly string _ts;

        public TamperedReader(
            SubjectIdentity subject,
            RunManifest manifest,
            ComplianceEvidence evidence)
        {
            _subject = subject;
            _manifest = manifest;
            _evidence = evidence;
            _ts = evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        }

        public bool IsAvailable => true;
        public string? WorkspaceRoot => "/stub";

        public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default) =>
            Task.FromResult(new SolutionInfo(_manifest.Solution.Id, _manifest.Solution.Name, "/stub"));

        public async IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(
            SubjectKind? kind = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (kind is null || kind == _subject.Kind)
                yield return new SubjectInfo(_subject);
            await Task.CompletedTask;
        }

        public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<RunManifest?>(string.Equals(runId, _manifest.Run.RunId, StringComparison.Ordinal) ? _manifest : null);

        public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<RunSummary?>(null);

        public IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default) =>
            EmptyAsync<ScenarioResult>();

        public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<AgentTrace?>(null);

        public IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default) =>
            EmptyAsync<RunManifest>();

        public IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default) =>
            EmptyAsync<RunPointer>();

        public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default) =>
            Task.FromResult<RunSummary?>(null);

        public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(new BaselineComparison(
                SubjectName: _subject.Name,
                Baseline: null,
                Current: new RunSummary("1.0", runId, "PASS", new RunStats(0, 0, 0, 0), new Dictionary<string, double>()),
                Deltas: new Dictionary<string, double>(),
                Regressed: false));

        public IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default) =>
            EmptyAsync<HistoryEntry>();

        public async IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(
            string? regulation = null,
            SubjectIdentity? subject = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if ((regulation is null || regulation == _evidence.Regulation) &&
                (subject is null || (subject.Kind == _subject.Kind && subject.Name == _subject.Name)))
            {
                yield return new ComplianceEvidencePointer(
                    Regulation: _evidence.Regulation,
                    SubjectName: _subject.Name,
                    Timestamp: _ts,
                    OverallStatus: _evidence.Summary.OverallStatus);
            }
            await Task.CompletedTask;
        }

        public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(
            string regulation,
            SubjectIdentity subject,
            string timestamp,
            CancellationToken ct = default)
        {
            if (regulation == _evidence.Regulation
                && subject.Kind == _subject.Kind
                && subject.Name == _subject.Name
                && timestamp == _ts)
                return Task.FromResult<ComplianceEvidence?>(_evidence);
            return Task.FromResult<ComplianceEvidence?>(null);
        }

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

#endif
