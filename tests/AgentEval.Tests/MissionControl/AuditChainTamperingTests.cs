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
        // condition) doesn't go silently unnoticed. Asserts on Cells.NotEmpty
        // too, because the empty-matrix branch ALSO returns AllChainsValid=true
        // — the assertion would otherwise pass even if no audit chain ran.
        const string sharedHash = "SHARED_HASH_VALID_AUDIT_CHAIN";
        var (subject, manifest, evidence) =
            BuildFixture(manifestHash: sharedHash, evidenceHash: sharedHash);

        var reader = new TamperedReader(subject, manifest, evidence);
        var service = new ComplianceMatrixService(reader);

        var matrix = await service.BuildMatrixAsync("test-reg", CancellationToken.None);

        Assert.True(matrix.AllChainsValid);
        Assert.NotEmpty(matrix.Subjects);
        Assert.NotEmpty(matrix.Cells);
    }

    [Fact]
    public async Task ComplianceMatrix_FilesystemTampering_FlipsAllChainsValidToFalse()
    {
        // Real-FS test: write evidence + manifest through the real
        // FileSystemOutputStore (which enforces hash matching at write-time),
        // then mutate manifest.json's ContentHash on disk to simulate
        // post-write tamper. The matrix read MUST detect the mismatch.
        //
        // Note on scope: the audit chain as currently implemented compares
        // the manifest's STORED `contentHash` field against the evidence's
        // recorded `sourceRun.manifestHash`. Tampering with OTHER manifest
        // fields (e.g. flipping a verdict from PASS to FAIL while leaving
        // the contentHash field intact) would NOT be detected because the
        // read path doesn't recompute the hash from the manifest's content
        // — that's a separate hardening (recompute-on-read) tracked
        // separately. This test pins down the contract the system *does*
        // enforce: hash-field tampering breaks the chain.
        var tempDir = Path.Combine(Path.GetTempPath(), "agenteval-audit-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var workspace = Path.Combine(tempDir, ".agenteval");
        var store = new FileSystemOutputStore(workspace);

        try
        {
            var solutionFile = Path.Combine(workspace, "solution.json");
            Directory.CreateDirectory(Path.GetDirectoryName(solutionFile)!);
            await File.WriteAllTextAsync(solutionFile,
                """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000099","name":"audit-fs-test"}""");

            var subject = new SubjectIdentity(SubjectKind.Agent, "AuditAgent");
            await store.EnsureSubjectAsync(subject);

            var manifest = await store.StartRunAsync(subject, new RunContext(
                EvalProject: "T", EvalProjectPath: "samples/T",
                Harness: "xunit", Seed: 1, ParentInvocationId: null, Kind: "eval"));

            await store.CompleteRunAsync(manifest, new RunSummary(
                SchemaVersion: "1.0",
                RunId: manifest.Run.RunId,
                Verdict: "PASS",
                Stats: new RunStats(1, 1, 0, 0),
                Metrics: new Dictionary<string, double>()));

            var sealedManifest = await store.GetRunManifestAsync(manifest.Run.RunId);
            Assert.NotNull(sealedManifest);
            var validHash = sealedManifest!.ContentHash;

            await store.SaveComplianceEvidenceAsync(
                "audit-fs",
                subject,
                new ComplianceEvidence(
                    SchemaVersion: "1.0",
                    Regulation: "audit-fs",
                    Subject: subject,
                    GeneratedAt: DateTimeOffset.UtcNow,
                    SourceRun: new SourceRunRef(manifest.Run.RunId, validHash),
                    Controls: new[]
                    {
                        new EvidenceControl("c-1", "Test", "pass", 1.0,
                            Array.Empty<string>(), Notes: null),
                    },
                    Summary: new EvidenceSummary(1, 1, 0, 0, "pass"),
                    Attestation: new Attestation("1.0", null, "stub", "stub")));

            // Sanity: chain valid before tampering.
            var serviceBefore = new ComplianceMatrixService(store);
            var matrixBefore = await serviceBefore.BuildMatrixAsync("audit-fs", CancellationToken.None);
            Assert.True(matrixBefore.AllChainsValid,
                "Pre-tamper matrix should validate cleanly — sanity check on the seed.");

            // Tamper: rewrite manifest.json's contentHash to a deliberately
            // bogus value. The chain check at ComplianceMatrixService.cs:113
            // string-compares the manifest's stored contentHash against the
            // evidence's recorded sourceRun.manifestHash — they now differ.
            var manifestPath = Directory.GetFiles(workspace, "manifest.json", SearchOption.AllDirectories).Single();
            var raw = await File.ReadAllTextAsync(manifestPath);
            // Pin the mutation to the exact contentHash field via regex so
            // a future change introducing the same hex string in another
            // field doesn't double-replace. Tolerates either compact or
            // pretty-printed JSON (System.Text.Json defaults to pretty here).
            var tampered = System.Text.RegularExpressions.Regex.Replace(
                raw,
                "\"contentHash\"\\s*:\\s*\"" + System.Text.RegularExpressions.Regex.Escape(validHash) + "\"",
                "\"contentHash\":\"TAMPERED_AFTER_WRITE\"");
            Assert.NotEqual(raw, tampered);
            await File.WriteAllTextAsync(manifestPath, tampered);

            // Sanity: after tampering, GetRunManifestAsync must see the new hash.
            var rereadManifest = await store.GetRunManifestAsync(manifest.Run.RunId);
            Assert.Equal("TAMPERED_AFTER_WRITE", rereadManifest!.ContentHash);

            var serviceAfter = new ComplianceMatrixService(store);
            var matrixAfter = await serviceAfter.BuildMatrixAsync("audit-fs", CancellationToken.None);

            Assert.False(matrixAfter.AllChainsValid,
                "Filesystem tampering of manifest.json contentHash must flip AllChainsValid to false " +
                "— the production read path was bypassed if this passes.");
            Assert.NotEmpty(matrixAfter.Cells);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
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

/// <summary>
/// Locks down the MC1.10.1 first-run landing contract: <c>Query.workspace</c>
/// reports <c>initialized: false</c> when the store is unavailable AND when
/// the store is available but no <c>solution.json</c> exists. The seeded
/// fixture covers <c>initialized: true</c> in <c>GraphQLReadResolversTests</c>;
/// this class covers the negative branches the resolver's try/catch was
/// added for.
/// </summary>
public class WorkspaceStateUninitialisedTests
{
    [Fact]
    public async Task Workspace_StoreUnavailable_ReportsInitializedFalse()
    {
        var reader = new UnavailableReader();
        var ws = await new AgentEval.MissionControl.GraphQL.Query().Workspace(reader, CancellationToken.None);

        Assert.False(ws.Initialized);
        Assert.Null(ws.Solution);
        Assert.False(string.IsNullOrEmpty(ws.AgentEvalVersion));
    }

    [Fact]
    public async Task Workspace_AvailableButNoSolution_ReportsInitializedFalse()
    {
        // Folder-exists-but-uninitialised path: IsAvailable=true,
        // EnsureSolutionAsync throws InvalidOperationException ("no solution.json").
        var reader = new ThrowingSolutionReader();
        var ws = await new AgentEval.MissionControl.GraphQL.Query().Workspace(reader, CancellationToken.None);

        Assert.False(ws.Initialized);
        Assert.Null(ws.Solution);
        Assert.Equal("/some-root", ws.Root);
    }

    private sealed class UnavailableReader : IOutputStoreReader
    {
        public bool IsAvailable => false;
        public string? WorkspaceRoot => null;
        public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("not initialised");
        public IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default) => EmptyAsync<SubjectInfo>();
        public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunManifest?>(null);
        public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunSummary?>(null);
        public IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default) => EmptyAsync<ScenarioResult>();
        public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default) => Task.FromResult<AgentTrace?>(null);
        public IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default) => EmptyAsync<RunManifest>();
        public IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default) => EmptyAsync<RunPointer>();
        public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default) => Task.FromResult<RunSummary?>(null);
        public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default) => EmptyAsync<HistoryEntry>();
        public IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default) => EmptyAsync<ComplianceEvidencePointer>();
        public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default) => Task.FromResult<ComplianceEvidence?>(null);

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingSolutionReader : IOutputStoreReader
    {
        public bool IsAvailable => true;
        public string? WorkspaceRoot => "/some-root";
        public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("solution.json not found at /some-root");
        public IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(SubjectKind? kind = null, CancellationToken ct = default) => EmptyAsync<SubjectInfo>();
        public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunManifest?>(null);
        public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunSummary?>(null);
        public IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(string runId, CancellationToken ct = default) => EmptyAsync<ScenarioResult>();
        public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default) => Task.FromResult<AgentTrace?>(null);
        public IAsyncEnumerable<RunManifest> ListRunsAsync(SubjectIdentity subject, CancellationToken ct = default) => EmptyAsync<RunManifest>();
        public IAsyncEnumerable<RunPointer> GetRecentRunsAsync(int count = 50, CancellationToken ct = default) => EmptyAsync<RunPointer>();
        public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default) => Task.FromResult<RunSummary?>(null);
        public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<HistoryEntry> GetHistoryAsync(SubjectIdentity subject, DateRange? range = null, CancellationToken ct = default) => EmptyAsync<HistoryEntry>();
        public IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(string? regulation = null, SubjectIdentity? subject = null, CancellationToken ct = default) => EmptyAsync<ComplianceEvidencePointer>();
        public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default) => Task.FromResult<ComplianceEvidence?>(null);

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

#endif
