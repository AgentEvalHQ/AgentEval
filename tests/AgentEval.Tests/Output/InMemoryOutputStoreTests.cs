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
