// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class FileSystemLayoutGoldenTest
{
    [Fact]
    public async Task Layout_Matches_Golden_Snapshot()
    {
        using var temp = TempWorkspace.Create("GoldenSolution");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        await store.EnsureSubjectAsync(subject);
        var ctx = new RunContext("Sample.Evals", "samples/Sample.Evals/", "TestHarness", 42, null, "eval");
        var manifest = await store.StartRunAsync(subject, ctx);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, MakeScenarioFixture("scenario1"));
        await store.CompleteRunAsync(manifest, MakeSummaryFixture("PASS", manifest.Run.RunId));

        var files = Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(temp.Path, f).Replace('\\', '/'))
            .Select(p => ReplaceRunId(p, manifest.Run.RunId, "{runId}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        await Verify(files);
    }

    private static string ReplaceRunId(string path, string realRunId, string placeholder)
        => path.Replace(realRunId, placeholder, StringComparison.Ordinal);

    private static ScenarioResult MakeScenarioFixture(string id) =>
        new(
            Id: id,
            Name: "Scenario One",
            Input: "test input",
            Output: "test output",
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double> { ["accuracy"] = 1.0 },
            Assertions: new[] { new AssertionResult("Contains keyword", true, null) },
            Duration: TimeSpan.FromSeconds(1),
            EstimatedCost: 0.001);

    private static RunSummary MakeSummaryFixture(string verdict, string runId) =>
        new(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: new RunStats(1, 1, 0, 0),
            Metrics: new Dictionary<string, double> { ["accuracy"] = 1.0 });
}
