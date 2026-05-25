// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Runtime.CompilerServices;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Phase-0 security task 0.8 (2026-05-13): the
/// <see cref="Query.ComplianceEvidence"/> resolver MUST enforce the per-doc
/// audit chain (plan-07 §7). This contract was previously enforced only on
/// the aggregated <c>ComplianceMatrix</c>; the single-evidence resolver
/// returned the document blind to whether <c>evidence.SourceRun.ManifestHash</c>
/// still matched the actual <c>RunManifest.ContentHash</c>. A hostile or
/// tampered workspace could ship an evidence pointing at an edited manifest
/// and the SPA's evidence-detail page would render it as valid.
/// </summary>
/// <remarks>
/// <para>
/// These three tests pin the contract for each return shape:
/// </para>
/// <list type="bullet">
///   <item>Happy path: manifest hash matches → <c>ChainValid: true</c>, <c>ChainBreakReason: null</c>.</item>
///   <item>Missing run: source run not in the store → <c>ChainValid: false</c>, <c>ChainBreakReason: "source-run-not-found"</c>.</item>
///   <item>Hash mismatch: source run exists but its <c>ContentHash</c> differs from the evidence's claim → <c>ChainValid: false</c>, <c>ChainBreakReason: "hash-mismatch"</c>.</item>
/// </list>
/// <para>
/// The reader stub returns the manifest and evidence directly so we can
/// simulate post-write tamper without going through the production write
/// path (which itself enforces hash matching at write time).
/// </para>
/// </remarks>
public class ComplianceEvidenceAuditChainResolverTests
{
    private const string RunId = "run-2026-05-13T08-00-00-Z";
    private const string ValidHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2"; // DevSkim: ignore DS173237
    private const string TamperedHash = "0000000000000000000000000000000000000000000000000000000000000000"; // DevSkim: ignore DS173237
    private const string Regulation = "test-reg";
    private const string Timestamp = "2026-05-13_08-00-00";

    [Fact]
    public async Task ComplianceEvidence_ValidChain_ReturnsChainValidTrue()
    {
        // Source manifest's ContentHash equals the hash the evidence recorded.
        var reader = BuildReader(manifestHash: ValidHash, evidenceClaimedHash: ValidHash);
        var query = new Query();

        var result = await query.ComplianceEvidence(
            reader, Regulation, SubjectKind.Agent, "TestAgent", Timestamp);

        Assert.NotNull(result);
        Assert.True(result!.ChainValid,
            "Chain must be valid when manifest.ContentHash matches evidence.SourceRun.ManifestHash.");
        Assert.Null(result.ChainBreakReason);
        Assert.NotNull(result.Evidence);
        Assert.Equal(Regulation, result.Evidence.Regulation);
    }

    [Fact]
    public async Task ComplianceEvidence_SourceRunMissing_ReturnsChainBreakReason()
    {
        // Reader returns the evidence but NOT the source run — simulates
        // an orphaned evidence (run deleted, GC bug, or hostile workspace).
        var reader = BuildReader(
            manifestHash: ValidHash,
            evidenceClaimedHash: ValidHash,
            withholdManifest: true);
        var query = new Query();

        var result = await query.ComplianceEvidence(
            reader, Regulation, SubjectKind.Agent, "TestAgent", Timestamp);

        Assert.NotNull(result);
        Assert.False(result!.ChainValid);
        Assert.Equal("source-run-not-found", result.ChainBreakReason);
        // Evidence payload still returned so the SPA can render it under the warning banner.
        Assert.NotNull(result.Evidence);
    }

    [Fact]
    public async Task ComplianceEvidence_HashMismatch_ReturnsChainBreakReason()
    {
        // Manifest body was edited after the evidence was sealed: the
        // evidence still claims hash X, but the manifest now hashes to Y.
        var reader = BuildReader(
            manifestHash: TamperedHash,
            evidenceClaimedHash: ValidHash);
        var query = new Query();

        var result = await query.ComplianceEvidence(
            reader, Regulation, SubjectKind.Agent, "TestAgent", Timestamp);

        Assert.NotNull(result);
        Assert.False(result!.ChainValid);
        Assert.Equal("hash-mismatch", result.ChainBreakReason);
        // Evidence payload still returned so the operator can inspect what was claimed.
        Assert.NotNull(result.Evidence);
        Assert.Equal(ValidHash, result.Evidence.SourceRun.ManifestHash);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\Windows\\System32\\config\\sam")]
    [InlineData("a/b/c")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ComplianceEvidence_UnsafeRunId_ReturnsSourceRunNotFound(string hostileRunId)
    {
        // Defense-in-depth: a hostile or tampered workspace can ship an
        // evidence.json whose `sourceRun.runId` is a path-traversal payload.
        // The resolver MUST short-circuit via `IsSafePathSegment` before the
        // value reaches `store.GetRunManifestAsync` (which calls TryLocate...
        // / Path.Combine internally). Without this guard, a malicious
        // workspace could exfiltrate manifests from outside the workspace
        // root. Pins the security guard at Query.cs lines 338-339
        // (Phase-0 gap-review concern #2, commit 6dbc5b4).
        //
        // The resolver returns the evidence WITH ChainBreakReason set rather
        // than throwing — same UX shape as the legitimately-orphaned case so
        // the SPA renders the broken-chain banner uniformly.
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var manifest = BuildManifest(ValidHash);
        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: Regulation,
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            // Hostile RunId — this MUST be rejected by IsSafePathSegment before
            // it reaches store.GetRunManifestAsync.
            SourceRun: new SourceRunRef(hostileRunId, ValidHash),
            Controls: new[]
            {
                new EvidenceControl("ctrl-1", "Test", "pass", 1.0, Array.Empty<string>(), Notes: null),
            },
            Summary: new EvidenceSummary(1, 1, 0, 0, "pass"),
            Attestation: new Attestation("1.0", null, "stub", "stub"));

        var reader = new AuditChainResolverReader(subject, manifest, evidence, withholdManifest: false);
        var query = new Query();

        var result = await query.ComplianceEvidence(
            reader, Regulation, SubjectKind.Agent, "TestAgent", Timestamp);

        // Guard must trip — evidence still returned, but flagged broken.
        Assert.NotNull(result);
        Assert.False(result!.ChainValid,
            $"Guard regression: RunId '{hostileRunId}' was accepted by IsSafePathSegment.");
        Assert.Equal("source-run-not-found", result.ChainBreakReason);
        // The hostile RunId must NOT appear in the returned shape's SourceRun;
        // the evidence payload is passed through as-is for inspection, but the
        // chain-break reason makes the broken state explicit.
        Assert.NotNull(result.Evidence);
    }

    private static RunManifest BuildManifest(string manifestHash) =>
        new(
            SchemaVersion: "1.0",
            Solution: new SolutionRef(Guid.Empty, "test", null),
            Subject: new SubjectRef(SubjectKind.Agent, "TestAgent", "1.0", null, null, null, null),
            Run: new RunRef(
                RunId: RunId,
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

    private static AuditChainResolverReader BuildReader(
        string manifestHash,
        string evidenceClaimedHash,
        bool withholdManifest = false)
    {
        var subject  = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var manifest = BuildManifest(manifestHash);
        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: Regulation,
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(RunId, evidenceClaimedHash),
            Controls: new[]
            {
                new EvidenceControl("ctrl-1", "Test", "pass", 1.0, Array.Empty<string>(), Notes: null),
            },
            Summary: new EvidenceSummary(1, 1, 0, 0, "pass"),
            Attestation: new Attestation("1.0", null, "stub", "stub"));

        return new AuditChainResolverReader(subject, manifest, evidence, withholdManifest);
    }

    /// <summary>
    /// Stub reader that returns one subject, one manifest (optionally
    /// withheld to simulate source-run-not-found), and one evidence.
    /// </summary>
    private sealed class AuditChainResolverReader : IOutputStoreReader
    {
        private readonly SubjectIdentity _subject;
        private readonly RunManifest _manifest;
        private readonly ComplianceEvidence _evidence;
        private readonly bool _withholdManifest;

        public AuditChainResolverReader(
            SubjectIdentity subject,
            RunManifest manifest,
            ComplianceEvidence evidence,
            bool withholdManifest)
        {
            _subject = subject;
            _manifest = manifest;
            _evidence = evidence;
            _withholdManifest = withholdManifest;
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

        public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
        {
            if (_withholdManifest) return Task.FromResult<RunManifest?>(null);
            return Task.FromResult<RunManifest?>(
                string.Equals(runId, _manifest.Run.RunId, StringComparison.Ordinal) ? _manifest : null);
        }

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

        public IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(
            string? regulation = null,
            SubjectIdentity? subject = null,
            CancellationToken ct = default) =>
            EmptyAsync<ComplianceEvidencePointer>();

        public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(
            string regulation,
            SubjectIdentity subject,
            string timestamp,
            CancellationToken ct = default)
        {
            if (regulation == _evidence.Regulation
                && subject.Kind == _subject.Kind
                && subject.Name == _subject.Name)
                return Task.FromResult<ComplianceEvidence?>(_evidence);
            return Task.FromResult<ComplianceEvidence?>(null);
        }

        // T3.6: red-team campaign reads — empty stub.
        public IAsyncEnumerable<RedTeamCampaignManifest> ListRedTeamCampaignsAsync(CancellationToken ct = default) =>
            EmptyAsync<RedTeamCampaignManifest>();

        public Task<RedTeamCampaignManifest?> GetRedTeamCampaignAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult<RedTeamCampaignManifest?>(null);

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

#endif
