// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.MissionControl.Services;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Regression: Phase-5 Task 5.1 — non-UTC workspace drill-down 404.
/// The bench writer uses local-clock <c>yyyy-MM-dd_HH-mm-ss</c> for the on-disk
/// timestamp directory; the SPA used to round-trip <c>lastEvidenceAt</c> through
/// JS <c>Date.toISOString()</c> which UTC-shifts the wall clock, producing a
/// URL that 404s against the directory. Fix: the resolver now publishes the
/// raw <c>ComplianceMatrixCell.Timestamp</c> field; the SPA reads it
/// verbatim. This test pins the resolver-side half of the contract.
/// </summary>
public class ComplianceMatrixTimezoneTests : IDisposable
{
    private readonly string _workspace;

    public ComplianceMatrixTimezoneTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "mc-tz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildMatrixAsync_NonUtcOffset_CellTimestampMatchesOnDiskDirectoryName()
    {
        // Arrange — seed evidence with a CET-summer offset (UTC+2).
        // The on-disk directory name uses the wall-clock 14:30, NOT the
        // UTC-shifted 12:30. Pre-fix, the SPA derived 12:30 from
        // lastEvidenceAt and 404'd on the cell click.
        var agentEvalDir = Path.Combine(_workspace, ".agenteval");
        Directory.CreateDirectory(agentEvalDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentEvalDir, "solution.json"),
            """{"schemaVersion":"1.0","id":"00000000-0000-0000-0000-000000000099","name":"tz-test"}""");

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TimezoneTestAgent");
        await store.EnsureSubjectAsync(subject);

        // Start + complete a run so we have a manifest hash to anchor evidence to.
        var generatedAt = new DateTimeOffset(2026, 5, 11, 14, 30, 0, TimeSpan.FromHours(2));
        var manifest = await store.StartRunAsync(subject, new RunContext(
            EvalProject: "tz.test",
            EvalProjectPath: "tests/tz",
            Harness: "xunit",
            Seed: 1,
            ParentInvocationId: null,
            Kind: "eval"));
        await store.CompleteRunAsync(manifest, new RunSummary(
            SchemaVersion: "1.0",
            RunId: manifest.Run.RunId,
            Verdict: "PASS",
            Stats: new RunStats(Total: 0, Passed: 0, Failed: 0, Warnings: 0),
            Metrics: new Dictionary<string, double>()));

        var sealedManifest = await store.GetRunManifestAsync(manifest.Run.RunId);
        Assert.NotNull(sealedManifest);

        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "gdpr",
            Subject: subject,
            GeneratedAt: generatedAt,
            SourceRun: new SourceRunRef(manifest.Run.RunId, sealedManifest!.ContentHash),
            Controls: new[]
            {
                new EvidenceControl("art-5", "Lawfulness", "pass", 1.0, Array.Empty<string>(), Notes: null),
            },
            Summary: new EvidenceSummary(ControlsTotal: 1, Passed: 1, Warnings: 0, Failed: 0, OverallStatus: "pass"),
            Attestation: new Attestation("test", null, "stub", "stub"));
        await store.SaveComplianceEvidenceAsync("gdpr", subject, evidence);

        // Sanity-check: the on-disk directory carries the LOCAL wall-clock string.
        var expectedTs = generatedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        Assert.Equal("2026-05-11_14-30-00", expectedTs);
        var onDisk = Path.Combine(agentEvalDir, "compliance", "gdpr", "TimezoneTestAgent", expectedTs);
        Assert.True(Directory.Exists(onDisk),
            $"Expected evidence directory at '{onDisk}' but it does not exist.");

        // Act — build the matrix the resolver feeds the SPA.
        var matrixService = new ComplianceMatrixService(store);
        var matrix = await matrixService.BuildMatrixAsync("gdpr");

        // Assert — exactly one cell, and its Timestamp matches the on-disk dir name.
        var cell = Assert.Single(matrix.Cells);
        Assert.Equal(expectedTs, cell.Timestamp);
        Assert.Equal("2026-05-11_14-30-00", cell.Timestamp);
        // The cell is reachable via /compliance/gdpr/agent/TimezoneTestAgent/2026-05-11_14-30-00
        // which matches the on-disk directory — drill-through succeeds.
    }
}

#endif
