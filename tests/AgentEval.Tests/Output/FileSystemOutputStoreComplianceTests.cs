// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class FileSystemOutputStoreComplianceTests : IDisposable
{
    private readonly string _root;

    public FileSystemOutputStoreComplianceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-compliance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteSolutionJson();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSolutionJson()
    {
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "TestSolution" };
        File.WriteAllText(
            Path.Combine(_root, "solution.json"),
            System.Text.Json.JsonSerializer.Serialize(solution,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    private static RunContext DefaultContext() =>
        new("MyProject", "/path/to/project", "xunit", null, null, "eval");

    private static SubjectIdentity DefaultSubject() =>
        new(SubjectKind.Agent, "ComplianceAgent");

    private async Task<(string RunId, string ContentHash)> CreateCompletedRunAsync(FileSystemOutputStore store)
    {
        var subject = DefaultSubject();
        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);
        var updated = await store.GetRunManifestAsync(runId);
        return (runId, updated!.ContentHash);
    }

    private static ComplianceEvidence MakeValidEvidence(string runId, string manifestHash) =>
        new(
            SchemaVersion: "1.0",
            Regulation: "SOC2",
            Subject: DefaultSubject(),
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(runId, manifestHash),
            Controls: new[]
            {
                new EvidenceControl("CC6.1", "Logical Access", "PASS", 1.0,
                    new[] { "scenario-01" }, null)
            },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.8.1", null, "AgentEval", "internal"));

    [Fact]
    public async Task SaveComplianceEvidence_NoSourceRun_Throws()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var evidence = MakeValidEvidence("nonexistent-run-id", "sha256:" + new string('0', 64));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveComplianceEvidenceAsync("SOC2", subject, evidence));
    }

    [Fact]
    public async Task SaveComplianceEvidence_HashMismatch_Throws()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var (runId, _) = await CreateCompletedRunAsync(store);

        var wrongHash = "sha256:" + new string('a', 64);
        var evidence = MakeValidEvidence(runId, wrongHash);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveComplianceEvidenceAsync("SOC2", subject, evidence));
        Assert.Contains("Manifest hash mismatch", ex.Message);
    }

    [Fact]
    public async Task SaveComplianceEvidence_ValidEvidence_WritesFile()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var (runId, contentHash) = await CreateCompletedRunAsync(store);
        var evidence = MakeValidEvidence(runId, contentHash);

        await store.SaveComplianceEvidenceAsync("SOC2", subject, evidence);

        var ts = evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var evidenceFile = Path.Combine(_root, "compliance", "SOC2", "ComplianceAgent", ts, "evidence.json");
        Assert.True(File.Exists(evidenceFile), $"Expected evidence file at {evidenceFile}");
    }

    [Fact]
    public async Task SaveComplianceEvidence_NullRegulation_Throws()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var (runId, hash) = await CreateCompletedRunAsync(store);
        var evidence = MakeValidEvidence(runId, hash);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveComplianceEvidenceAsync("", subject, evidence));
    }
}
