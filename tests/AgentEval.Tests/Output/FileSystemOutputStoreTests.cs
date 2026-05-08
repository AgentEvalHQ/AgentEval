// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class FileSystemOutputStoreTests : IDisposable
{
    private readonly string _root;

    public FileSystemOutputStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-fsos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSolutionJson(string? name = "TestSolution")
    {
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name };
        File.WriteAllText(
            Path.Combine(_root, "solution.json"),
            JsonSerializer.Serialize(solution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static RunContext DefaultContext() =>
        new("MyProject", "/path/to/project", "xunit", null, null, "eval");

    private static SubjectIdentity DefaultSubject() =>
        new(SubjectKind.Agent, "TestAgent");

    [Fact]
    public async Task EnsureSolution_Throws_WhenSolutionFileMissing()
    {
        var store = new FileSystemOutputStore(_root);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureSolutionAsync());
    }

    [Fact]
    public async Task EnsureSubject_CreatesSubjectFile()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var info = await store.EnsureSubjectAsync(subject);

        Assert.Equal(subject.Name, info.Identity.Name);
        Assert.Equal(subject.Kind, info.Identity.Kind);

        var subjectFile = Path.Combine(_root, "subjects", "agents", "TestAgent", "subject.json");
        Assert.True(File.Exists(subjectFile));
    }

    [Fact]
    public async Task StartRun_CreatesRunDirAndManifest()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());

        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("TestAgent", manifest.Subject.Name);
        Assert.Equal("PENDING", manifest.Run.Verdict);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_[a-f0-9]{8}$", manifest.Run.RunId);

        var runDir = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId);
        Assert.True(Directory.Exists(runDir));

        var manifestFile = Path.Combine(runDir, "manifest.json");
        Assert.True(File.Exists(manifestFile));
    }

    [Fact]
    public async Task CompleteRun_WritesSummaryAndUpdatesManifestWithHash()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var summary = new RunSummary("1.0", manifest.Run.RunId, "PASS",
            new RunStats(5, 4, 1, 0),
            new Dictionary<string, double> { ["accuracy"] = 0.8 });

        await store.CompleteRunAsync(manifest, summary, CancellationToken.None);

        var runDir = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId);
        var summaryFile = Path.Combine(runDir, "summary.json");
        Assert.True(File.Exists(summaryFile));

        var updatedManifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "manifest.json"));
        Assert.Contains("sha256:", updatedManifestJson);
        Assert.Matches(@"sha256:[a-f0-9]{64}", updatedManifestJson);
        Assert.DoesNotContain("sha256:00000000000000000000000000000000000000000000000000000000000000", updatedManifestJson);
    }

    [Fact]
    public async Task RoundTrip_StartWriteScenarioComplete_PersistsAll()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();

        var manifest = await store.StartRunAsync(subject, DefaultContext());
        var runId = manifest.Run.RunId;

        var scenario = new ScenarioResult(
            "scenario-01", "Test Scenario", "input", "output", true, 1.0,
            new Dictionary<string, double> { ["score"] = 1.0 },
            new List<AssertionResult> { new("assert1", true, null) },
            TimeSpan.FromSeconds(1), 0.001);

        await store.WriteScenarioResultAsync(runId, scenario);

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["score"] = 1.0 });
        await store.CompleteRunAsync(manifest, summary);

        var retrievedManifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(retrievedManifest);
        Assert.Equal("PASS", retrievedManifest!.Run.Verdict);
        Assert.Equal(1, retrievedManifest.Run.ScenarioCount);

        var retrievedSummary = await store.GetRunSummaryAsync(runId);
        Assert.NotNull(retrievedSummary);
        Assert.Equal("PASS", retrievedSummary!.Verdict);

        var scenarios = new List<ScenarioResult>();
        await foreach (var s in store.GetScenarioResultsAsync(runId))
            scenarios.Add(s);

        Assert.Single(scenarios);
        Assert.Equal("scenario-01", scenarios[0].Id);
    }
}
