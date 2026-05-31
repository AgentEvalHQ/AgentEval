// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class InMemoryOutputStoreTests
{
    private static RunContext DefaultContext() =>
        new("MyProject", "/path/to/project", "xunit", null, null, "eval");

    [Fact]
    public async Task RoundTrip_StartCompleteGet_ReturnsSameData()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;

        var scenario = new ScenarioResult(
            "s1", "Scenario 1", "input text", "output text", true, 0.95,
            new Dictionary<string, double> { ["accuracy"] = 0.95 },
            new List<AssertionResult> { new("check1", true, null) },
            TimeSpan.FromMilliseconds(500), 0.002);

        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["accuracy"] = 0.95 });
        await store.CompleteRunAsync(manifest, summary);

        var retrievedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(retrievedManifest);
        Assert.Equal("PASS", retrievedManifest!.Run.Verdict);
        Assert.Equal(1, retrievedManifest.Run.ScenarioCount);
        Assert.Matches(@"^sha256:[a-f0-9]{64}$", retrievedManifest.ContentHash);

        var retrievedSummary = await store.GetRunSummaryAsync(runId);
        Assert.NotNull(retrievedSummary);
        Assert.Equal("PASS", retrievedSummary!.Verdict);
        Assert.Equal(0.95, retrievedSummary.Metrics["accuracy"]);

        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId))
            scenarios.Add(s);

        Assert.Single(scenarios);
        Assert.Equal("s1", scenarios[0].Id);
        Assert.True(scenarios[0].Passed);
    }

    [Fact]
    public async Task ListSubjects_FiltersByKind()
    {
        var store = new InMemoryOutputStore();
        var agent1 = new SubjectIdentity(SubjectKind.Agent, "Agent1");
        var agent2 = new SubjectIdentity(SubjectKind.Agent, "Agent2");
        var workflow1 = new SubjectIdentity(SubjectKind.Workflow, "Workflow1");

        await store.EnsureSubjectAsync(agent1);
        await store.EnsureSubjectAsync(agent2);
        await store.EnsureSubjectAsync(workflow1);

        var agents = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Agent))
            agents.Add(s);

        var workflows = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Workflow))
            workflows.Add(s);

        Assert.Equal(2, agents.Count);
        Assert.Single(workflows);
        Assert.All(agents, s => Assert.Equal(SubjectKind.Agent, s.Identity.Kind));
        Assert.All(workflows, s => Assert.Equal(SubjectKind.Workflow, s.Identity.Kind));
    }

    [Fact]
    public async Task SaveComplianceEvidence_NoSourceRun_Throws()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "SOC2",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("nonexistent", "sha256:" + new string('0', 64)),
            Controls: new[] { new EvidenceControl("CC6.1", "Logical Access", "PASS", 1.0, new[] { "s1" }, null) },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval", "internal"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveComplianceEvidenceAsync("SOC2", subject, evidence));
    }

    [Fact]
    public async Task SaveComplianceEvidence_HashMismatch_Throws()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComplianceAgent");
        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(1, 1, 0, 0), new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        // Use a wrong hash — must mismatch the run's actual ContentHash
        var wrongHash = "sha256:" + new string('a', 64);
        var evidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "SOC2",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(manifest.Run.RunId, wrongHash),
            Controls: new[] { new EvidenceControl("CC6.1", "Logical Access", "PASS", 1.0, new[] { "s1" }, null) },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval", "internal"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveComplianceEvidenceAsync("SOC2", subject, evidence));
        Assert.Contains("Manifest hash mismatch", ex.Message);
    }

    [Fact]
    public async Task CompleteRun_RecentRunPointer_PopulatesAllAdditiveFields()
    {
        // BUG-32: InMemoryOutputStore built RunPointer with only the 4 v1.0 positional args,
        // leaving Kind/Score/DurationMs/EstimatedCost null — so the documented test double returned
        // a different shape than FileSystemOutputStore. All four must now be populated.
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Workflow, "ParitySubject");

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(Total: 2, Passed: 1, Failed: 1, Warnings: 0),
            new Dictionary<string, double> { ["score"] = 0.5 },
            Cost: new RunCostInfo(EstimatedCost: 0.0123, PromptTokens: 100, CompletionTokens: 50));
        await store.CompleteRunAsync(manifest, summary);

        var recent = new List<RunPointer>();
        await foreach (var rp in store.GetRecentRunsAsync())
            recent.Add(rp);

        var pointer = Assert.Single(recent);
        Assert.Equal(SubjectKind.Workflow, pointer.Kind);
        Assert.Equal(0.5, pointer.Score);                 // Passed/Total = 1/2
        Assert.NotNull(pointer.DurationMs);
        Assert.True(pointer.DurationMs >= 0);
        Assert.Equal(0.0123, pointer.EstimatedCost);
    }

    [Fact]
    public async Task History_AppendAndReadBack()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "HistoryAgent");

        var entry1 = new HistoryEntry("run-001", DateTimeOffset.UtcNow.AddDays(-2), "PASS", 0.9, null);
        var entry2 = new HistoryEntry("run-002", DateTimeOffset.UtcNow.AddDays(-1), "FAIL", 0.4, "regression");

        await store.AppendHistoryEntryAsync(subject, entry1);
        await store.AppendHistoryEntryAsync(subject, entry2);

        var history = new List<HistoryEntry>();
        await foreach (var e in store.GetHistoryAsync(subject))
            history.Add(e);

        Assert.Equal(2, history.Count);
        Assert.Equal("run-001", history[0].RunId);
        Assert.Equal("run-002", history[1].RunId);
    }
}
