// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.MissionControl.Services;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Pins plan-08 portal-review A1 (2026-05-24): per-cell <c>ChainValid</c> +
/// <c>ChainBreakReason</c> propagate from <see cref="ComplianceMatrixService.BuildMatrixAsync"/>
/// so a tampered cell renders with a tamper overlay in the SPA matrix instead of
/// silently showing its pre-tamper status as the green ✓.
/// </summary>
public class ComplianceMatrixCellChainValidTests : IDisposable
{
    private readonly string _workspace;

    public ComplianceMatrixCellChainValidTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "mc-cell-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildMatrixAsync_ValidChain_CellReportsChainValidTrue()
    {
        // Arrange — seed real evidence pointing at an existing run with a matching hash.
        var (store, subject) = await SeedSolutionAsync();
        var (manifest, _) = await SeedRunAsync(store, subject);
        await SeedEvidenceAsync(store, subject, sourceRunId: manifest.Run.RunId,
            sourceRunHash: manifest.ContentHash);

        // Act
        var matrixService = new ComplianceMatrixService(store);
        var matrix = await matrixService.BuildMatrixAsync("gdpr");

        // Assert — valid chain on every cell + matrix-wide aggregate.
        Assert.True(matrix.AllChainsValid);
        Assert.NotEmpty(matrix.Cells);
        foreach (var cell in matrix.Cells)
        {
            Assert.True(cell.ChainValid, $"Cell ({cell.SubjectName}, {cell.ControlId}) expected ChainValid=true");
            Assert.Null(cell.ChainBreakReason);
        }
    }

    [Fact]
    public async Task BuildMatrixAsync_PostHocHashTamper_CellReportsHashMismatch()
    {
        // Arrange — write evidence honestly (the API rejects invalid hashes at write time,
        // which is the correct runtime behaviour), THEN simulate a filesystem-level attacker
        // who edits the run-manifest content after the fact. The resolver re-checks on read,
        // so it should now detect the broken chain.
        var (store, subject) = await SeedSolutionAsync();
        var (manifest, _) = await SeedRunAsync(store, subject);
        await SeedEvidenceAsync(store, subject, sourceRunId: manifest.Run.RunId,
            sourceRunHash: manifest.ContentHash);

        // Tamper: rewrite manifest.json under the run dir. Its new ContentHash will differ
        // from the value the evidence captured. The resolver re-reads the manifest and
        // compares ContentHash to evidence.SourceRun.ManifestHash on every BuildMatrixAsync.
        TamperWithRunManifest(store, manifest.Run.RunId);

        // Act
        var matrixService = new ComplianceMatrixService(store);
        var matrix = await matrixService.BuildMatrixAsync("gdpr");

        // Assert
        Assert.False(matrix.AllChainsValid);
        Assert.NotEmpty(matrix.Cells);
        foreach (var cell in matrix.Cells)
        {
            Assert.False(cell.ChainValid, $"Cell ({cell.SubjectName}, {cell.ControlId}) expected ChainValid=false");
            Assert.Equal("hash-mismatch", cell.ChainBreakReason);
        }
    }

    [Fact]
    public async Task BuildMatrixAsync_PostHocRunDeleted_CellReportsSourceRunNotFound()
    {
        // Arrange — same shape as the hash-tamper test but the run directory disappears
        // entirely after the fact. The resolver's GetRunManifestAsync returns null;
        // ChainBreakReason should reflect that distinct condition.
        var (store, subject) = await SeedSolutionAsync();
        var (manifest, _) = await SeedRunAsync(store, subject);
        await SeedEvidenceAsync(store, subject, sourceRunId: manifest.Run.RunId,
            sourceRunHash: manifest.ContentHash);

        DeleteRunDirectory(store, subject, manifest.Run.RunId);

        // Act
        var matrixService = new ComplianceMatrixService(store);
        var matrix = await matrixService.BuildMatrixAsync("gdpr");

        // Assert
        Assert.False(matrix.AllChainsValid);
        Assert.NotEmpty(matrix.Cells);
        foreach (var cell in matrix.Cells)
        {
            Assert.False(cell.ChainValid);
            Assert.Equal("source-run-not-found", cell.ChainBreakReason);
        }
    }

    private void TamperWithRunManifest(FileSystemOutputStore store, string runId)
    {
        // The v0.10.1 chain check compares evidence.SourceRun.ManifestHash against the
        // value stored INSIDE manifest.json's "contentHash" field (not a recompute on read —
        // that's T1.7's canonical-JSON re-hash story). To simulate a tamper the current
        // chain check catches, edit the contentHash field directly to a different value.
        // Real-world attack vector: an attacker who can write to .agenteval/ but doesn't
        // know the evidence hash format would naturally end up changing this field while
        // tampering elsewhere.
        var subjectsDir = Path.Combine(store.WorkspaceRoot!, "subjects", "agents");
        foreach (var agentDir in Directory.EnumerateDirectories(subjectsDir))
        {
            var runDir = Path.Combine(agentDir, "runs", runId);
            var manifestPath = Path.Combine(runDir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var body = File.ReadAllText(manifestPath);
            // Replace the stored ContentHash with a different value. Regex picks the first
            // sha256 hash field (which is "contentHash").
            var tampered = System.Text.RegularExpressions.Regex.Replace(
                body,
                "\"contentHash\":\\s*\"sha256:[0-9a-f]+\"",
                "\"contentHash\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (tampered == body)
                throw new InvalidOperationException(
                    $"Test tamper failed — no contentHash field found in {manifestPath}. " +
                    "Check the manifest schema; tamper helper may need updating.");
            File.WriteAllText(manifestPath, tampered);
            return;
        }
        throw new InvalidOperationException($"No run dir found for runId={runId}");
    }

    private void DeleteRunDirectory(FileSystemOutputStore store, SubjectIdentity subject, string runId)
    {
        var runDir = Path.Combine(store.WorkspaceRoot!, "subjects", "agents", subject.Name, "runs", runId);
        if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task<(FileSystemOutputStore Store, SubjectIdentity Subject)> SeedSolutionAsync()
    {
        var agentEvalDir = Path.Combine(_workspace, ".agenteval");
        Directory.CreateDirectory(agentEvalDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentEvalDir, "solution.json"),
            """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000099","name":"chain-test"}""");

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "ChainTestAgent");
        await store.EnsureSubjectAsync(subject);
        return (store, subject);
    }

    private static async Task<(RunManifest Manifest, RunSummary Summary)> SeedRunAsync(
        FileSystemOutputStore store, SubjectIdentity subject)
    {
        var manifest = await store.StartRunAsync(subject, new RunContext(
            EvalProject: "chain.test",
            EvalProjectPath: "tests/chain",
            Harness: "xunit",
            Seed: 1,
            ParentInvocationId: null,
            Kind: "eval"));
        var summary = new RunSummary(
            SchemaVersion: "1.0",
            RunId: manifest.Run.RunId,
            Verdict: "PASS",
            Stats: new RunStats(Total: 0, Passed: 0, Failed: 0, Warnings: 0),
            Metrics: new Dictionary<string, double>());
        await store.CompleteRunAsync(manifest, summary);

        // Re-fetch sealed manifest (CompleteRunAsync updates ContentHash).
        var sealedManifest = await store.GetRunManifestAsync(manifest.Run.RunId);
        return (sealedManifest!, summary);
    }

    private static async Task SeedEvidenceAsync(
        FileSystemOutputStore store, SubjectIdentity subject,
        string sourceRunId, string sourceRunHash)
    {
        var generatedAt = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "gdpr",
            Subject: subject,
            GeneratedAt: generatedAt,
            SourceRun: new SourceRunRef(sourceRunId, sourceRunHash),
            Controls: new[]
            {
                new EvidenceControl("art-5", "Lawfulness", "pass", 1.0, Array.Empty<string>(), Notes: null),
                new EvidenceControl("art-6", "Consent", "pass", 0.95, Array.Empty<string>(), Notes: null),
            },
            Summary: new EvidenceSummary(ControlsTotal: 2, Passed: 2, Warnings: 0, Failed: 0, OverallStatus: "pass"),
            Attestation: new Attestation("test", null, "stub", "stub"));
        await store.SaveComplianceEvidenceAsync("gdpr", subject, evidence);
    }
}

#endif
