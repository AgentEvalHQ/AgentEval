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
    public async Task SubjectName_WithPathTraversalChars_StaysContainedUnderRoot()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var evil = new SubjectIdentity(SubjectKind.Agent, "../../etc/passwd");

        await store.EnsureSubjectAsync(evil);

        var written = Directory.EnumerateFiles(_root, "subject.json", SearchOption.AllDirectories).ToList();
        Assert.Single(written);

        var rootFull = Path.GetFullPath(_root);
        var writtenFull = Path.GetFullPath(written[0]);
        Assert.StartsWith(rootFull, writtenFull, StringComparison.OrdinalIgnoreCase);

        var rel = Path.GetRelativePath(_root, writtenFull);
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"subjects{sep}agents", Path.GetDirectoryName(Path.GetDirectoryName(rel)));
    }

    [Fact]
    public async Task ListSubjects_FiltersByKind()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "Alpha"));
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Agent, "Beta"));
        await store.EnsureSubjectAsync(new SubjectIdentity(SubjectKind.Workflow, "Gamma"));

        var agents = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Agent))
            agents.Add(s);

        var workflows = new List<SubjectInfo>();
        await foreach (var s in store.ListSubjectsAsync(SubjectKind.Workflow))
            workflows.Add(s);

        Assert.Equal(2, agents.Count);
        Assert.Single(workflows);
        Assert.Contains(agents, a => a.Identity.Name == "Alpha");
        Assert.Contains(agents, a => a.Identity.Name == "Beta");
        Assert.Contains(workflows, w => w.Identity.Name == "Gamma");
    }

    [Fact]
    public async Task RedTeamCampaign_StartAndComplete_PersistsManifestAndFindings()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var ctx = new RedTeamCampaignContext("My Campaign",
            new[] { new SubjectIdentity(SubjectKind.Agent, "TargetAgent") }, "lite");

        var manifest = await store.StartRedTeamCampaignAsync(ctx);
        Assert.Equal("My Campaign", manifest.Name);
        Assert.Matches(@"^My Campaign_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", manifest.CampaignId);

        await store.CompleteRedTeamCampaignAsync(manifest.CampaignId,
            new RedTeamFindings(new List<object> { new { id = "f1", severity = "low" } }));

        var redTeamRoot = Path.Combine(_root, "red-team");
        var findingsFiles = Directory.EnumerateFiles(redTeamRoot, "findings.json", SearchOption.AllDirectories).ToList();
        Assert.Single(findingsFiles);
    }

    [Fact]
    public async Task StartRun_InitialManifest_ValidatesAgainstSchema()
    {
        WriteSolutionJson();
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var manifest = await store.StartRunAsync(subject, DefaultContext());

        var manifestPath = Path.Combine(_root, "subjects", "agents", "TestAgent", "runs", manifest.Run.RunId, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".manifest.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var schema = Json.Schema.JsonSchema.FromText(reader.ReadToEnd());

        var result = schema.Evaluate(System.Text.Json.Nodes.JsonNode.Parse(json),
            new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        Assert.True(result.IsValid,
            string.Join("\n", result.Details.Where(d => !d.IsValid)
                .Select(d => $"{d.EvaluationPath}: {string.Join(", ", d.Errors?.Select(e => $"{e.Key}={e.Value}") ?? Array.Empty<string>())}")));
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
