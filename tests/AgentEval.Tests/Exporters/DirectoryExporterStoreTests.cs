// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Exporters;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Exporters;

public class DirectoryExporterStoreTests
{
    [Fact]
    public async Task ExportThroughStore_WritesScenariosAndCompletesRun()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var context = new RunContext("TestProject", ".", "TestHarness", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, context);

        var report = new EvaluationReport
        {
            RunId = manifest.Run.RunId,
            Name = "Store export test",
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-5),
            EndTime = DateTimeOffset.UtcNow,
            TotalTests = 2,
            PassedTests = 1,
            FailedTests = 1,
            SkippedTests = 0,
            OverallScore = 50.0,
            TestResults =
            [
                new TestResultSummary
                {
                    Name = "scenario-pass",
                    Passed = true,
                    Score = 100,
                    DurationMs = 200,
                    MetricScores = new Dictionary<string, double> { ["accuracy"] = 1.0 }
                },
                new TestResultSummary
                {
                    Name = "scenario-fail",
                    Passed = false,
                    Score = 0,
                    DurationMs = 100,
                    MetricScores = new Dictionary<string, double> { ["accuracy"] = 0.0 }
                }
            ]
        };

        var exporter = new DirectoryExporter();
        await exporter.ExportThroughStoreAsync(store, subject, manifest, report);

        var summary = await store.GetRunSummaryAsync(manifest.Run.RunId);
        Assert.NotNull(summary);
        Assert.Equal("FAIL", summary.Verdict);
        Assert.Equal(2, summary.Stats.Total);
        Assert.Equal(1, summary.Stats.Passed);
        Assert.Equal(1, summary.Stats.Failed);

        var scenarios = new List<ScenarioResult>();
        await foreach (var sr in store.GetScenarioResultsAsync(manifest.Run.RunId))
            scenarios.Add(sr);

        Assert.Equal(2, scenarios.Count);
    }

    [Fact]
    public async Task ExportThroughStore_AllPassed_ReturnsPassVerdict()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var context = new RunContext("TestProject", ".", "TestHarness", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, context);

        var report = new EvaluationReport
        {
            TotalTests = 2,
            PassedTests = 2,
            FailedTests = 0,
            SkippedTests = 0,
            TestResults =
            [
                new TestResultSummary { Name = "s1", Passed = true, Score = 100 },
                new TestResultSummary { Name = "s2", Passed = true, Score = 100 }
            ]
        };

        var exporter = new DirectoryExporter();
        await exporter.ExportThroughStoreAsync(store, subject, manifest, report);

        var summary = await store.GetRunSummaryAsync(manifest.Run.RunId);
        Assert.NotNull(summary);
        Assert.Equal("PASS", summary.Verdict);
    }

    [Fact]
    public async Task ExportThroughStore_MetricsComputedAsMeans()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var context = new RunContext("TestProject", ".", "TestHarness", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, context);

        var report = new EvaluationReport
        {
            TotalTests = 2,
            PassedTests = 2,
            FailedTests = 0,
            SkippedTests = 0,
            TestResults =
            [
                new TestResultSummary
                {
                    Name = "s1", Passed = true, Score = 100,
                    MetricScores = new Dictionary<string, double> { ["faithfulness"] = 0.8 }
                },
                new TestResultSummary
                {
                    Name = "s2", Passed = true, Score = 100,
                    MetricScores = new Dictionary<string, double> { ["faithfulness"] = 0.6 }
                }
            ]
        };

        var exporter = new DirectoryExporter();
        await exporter.ExportThroughStoreAsync(store, subject, manifest, report);

        var summary = await store.GetRunSummaryAsync(manifest.Run.RunId);
        Assert.NotNull(summary);
        Assert.True(summary.Metrics.ContainsKey("faithfulness"));
        Assert.Equal(0.7, summary.Metrics["faithfulness"], precision: 10);
    }

    [Fact]
    public async Task ExportThroughStore_NullStore_Throws()
    {
        var exporter = new DirectoryExporter();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var manifest = BuildNullManifest(subject);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            exporter.ExportThroughStoreAsync(null!, subject, manifest, new EvaluationReport()));
    }

    private static RunManifest BuildNullManifest(SubjectIdentity subject)
    {
        return new RunManifest(
            SchemaVersion: "1.0",
            Solution: new SolutionRef(Guid.Empty, "test", null),
            Subject: new SubjectRef(subject.Kind, subject.Name, null, null, null, null, null),
            Run: new RunRef("run1", DateTimeOffset.UtcNow, TimeSpan.Zero, 0, "PENDING", "eval", "p", ".", "h", null, null),
            Git: new GitRef(null, null, false, null),
            AgentEval: new AgentEvalRef("0.0.0", null),
            Environment: new EnvRef(Environment.MachineName, "", "", false, null, null),
            ContentHash: "sha256:0000000000000000000000000000000000000000000000000000000000000000");
    }
}
