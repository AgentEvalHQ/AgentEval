// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.Tests.Cli;

public sealed class StoreV1WorkspaceImportTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-workspace-import-test-");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_WorkspaceRun_PreservesRichReportOnlyWithCapture(bool capture)
    {
        var (workspace, run, evidence, reportBytes) = await CreateAsync();
        var before = SourceHashes(workspace);
        var destination = Path.Combine(_root.FullName, "aef");
        var stderr = new StringWriter();
        var exit = await AefCommand.RunExportAsync(run, destination, null, "mocked", capture ? "on" : "off",
            false, null, true, TextWriter.Null, stderr);
        Assert.True(exit == ExitCodes.Success, stderr.ToString());
        var imported = new AefComplianceArtifactReader().Read(destination);
        Assert.True(imported.Run.Imported);
        Assert.Equal(evidence.Base.SourceRun.RunId, imported.Run.RunId);
        Assert.Equal("intact", imported.Run.Integrity);
        Assert.Equal(capture ? 2 : 1, imported.Documents.Count);
        if (capture)
            Assert.Equal(System.Text.Encoding.UTF8.GetString(reportBytes), Assert.Single(imported.Documents, d => d.IsRich).Document.GetRawText());
        else Assert.DoesNotContain(imported.Documents, d => d.IsRich);
        Assert.Equal(before, SourceHashes(workspace));
        var results = File.ReadAllText(Path.Combine(destination, "results.ndjson"));
        Assert.Contains("E-compliance-1", results);
        Assert.Equal(capture, results.Contains("E-compliance-original-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_SelectedRunFolderWithDuplicateId_UsesExactlyThatFolder()
    {
        var (workspace, first, evidence, reportBytes) = await CreateAsync();
        var second = DuplicateRun(workspace, first, evidence.Base.SourceRun.RunId);
        foreach (var (source, name) in new[] { (first, "WorkspaceAgent"), (second, "OtherAgent") })
        {
            var destination = Path.Combine(_root.FullName, "aef-" + name);
            var exit = await AefCommand.RunExportAsync(source, destination, null, "mocked", "on", false, null, true, TextWriter.Null, TextWriter.Null);
            Assert.Equal(ExitCodes.Success, exit);
            var header = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "run.json")))!;
            Assert.Equal("agent:" + name, header["subject"]!["ref"]!.GetValue<string>());
            var imported = new AefComplianceArtifactReader().Read(destination);
            if (source == first)
            {
                Assert.Equal(2, imported.Documents.Count);
                Assert.All(imported.Documents, document => Assert.Equal(evidence.Base.SourceRun, document.Evidence.SourceRun));
                Assert.Equal(System.Text.Encoding.UTF8.GetString(reportBytes), Assert.Single(imported.Documents, d => d.IsRich).Document.GetRawText());
            }
            else Assert.Empty(imported.Documents); // Same run ID cannot attach another subject's report.
        }
    }

    [Fact]
    public async Task Export_WorkspaceIdOnlyIsAmbiguous_RefusesBeforeOutput()
    {
        var (workspace, first, evidence, _) = await CreateAsync();
        DuplicateRun(workspace, first, evidence.Base.SourceRun.RunId);
        var destination = Path.Combine(_root.FullName, "aef");
        var stderr = new StringWriter();
        var exit = await AefCommand.RunExportAsync(workspace, destination, evidence.Base.SourceRun.RunId,
            "mocked", "on", false, null, true, TextWriter.Null, stderr);
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("ambiguous", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("conflicting-base")]
    [InlineData("conflicting-identity")]
    public async Task Export_ConflictingCompliance_RefusesBeforeOutput(string conflict)
    {
        var (workspace, run, _, _) = await CreateAsync();
        var rich = Directory.GetFiles(workspace, "gdpr-evidence.json", SearchOption.AllDirectories).Single();
        var generic = Path.Combine(Path.GetDirectoryName(rich)!, "evidence.json");
        if (conflict == "hash")
        {
            ChangeJson(generic, j => j["sourceRun"]!["manifestHash"] = "sha256:" + new string('a', 64));
            ChangeJson(rich, j => j["base"]!["sourceRun"]!["manifestHash"] = "sha256:" + new string('a', 64));
        }
        else if (conflict == "conflicting-base") ChangeJson(rich, j => j["base"]!["controls"]![0]!["title"] = "Changed");
        else ChangeJson(rich, j => j["base"]!["sourceRun"]!["runId"] = "different-run");
        var destination = Path.Combine(_root.FullName, "aef");
        var error = Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination));
        Assert.Contains(conflict == "hash" ? "hash disagrees" : conflict == "conflicting-base" ? "disagree" : "different evidence identities", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("subject")]
    [InlineData("kind")]
    public async Task Export_UnrelatedCompliance_DoesNotAttachIt(string identity)
    {
        var (workspace, run, _, _) = await CreateAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(workspace, "compliance"), "*.json", SearchOption.AllDirectories))
            ChangeJson(file, j =>
            {
                var basis = j["base"] ?? j;
                if (identity == "run") basis["sourceRun"]!["runId"] = "unrelated-run";
                else if (identity == "subject") basis["subject"]!["name"] = "DifferentAgent";
                else basis["subject"]!["kind"] = "workflow";
            });
        var destination = Path.Combine(_root.FullName, "aef");
        StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination);
        Assert.Empty(new AefComplianceArtifactReader().Read(destination).Documents);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("kind")]
    public async Task Export_ManifestDisagreesWithSelectedFolder_Refuses(string identity)
    {
        var (workspace, run, _, _) = await CreateAsync();
        ChangeJson(Path.Combine(run, "manifest.json"), j =>
        {
            if (identity == "run") j["run"]!["runId"] = "another-run";
            else j["subject"]!["kind"] = "workflow";
        });
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, run, Path.Combine(_root.FullName, "aef")));
    }

    [Fact]
    public async Task Export_InterruptedRunWithoutScenarios_RecordsAbortedImport()
    {
        var workspace = Path.Combine(_root.FullName, ".agenteval");
        var store = new FileSystemOutputStore(workspace);
        await store.InitializeSolutionAsync("fixture");
        var subject = new SubjectIdentity(SubjectKind.Agent, "Interrupted");
        var manifest = await store.StartRunAsync(subject, new RunContext("Fixture", "tests/fixture", "fixture", null, null, "benchmark"));
        var run = store.ResolveRunDirectory(subject, manifest.Run.RunId);
        var destination = Path.Combine(_root.FullName, "aef");
        StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination);
        var imported = new AefComplianceArtifactReader().Read(destination);
        Assert.Equal("aborted", imported.Run.Status);
        Assert.True(imported.Run.Imported);
        Assert.Contains("never completed", imported.Run.AbortReason);
        Assert.Empty(File.ReadAllLines(Path.Combine(destination, "results.ndjson")));
    }

    [Fact]
    public async Task Export_OutsideWorkspaceOrMissingManifest_RefusesBeforeOutput()
    {
        var (workspace, run, _, _) = await CreateAsync();
        var destination = Path.Combine(_root.FullName, "aef");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Path.Combine(_root.FullName, "other"), run, destination));
        File.Delete(Path.Combine(run, "manifest.json"));
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Resolve_NoMatchingRun_Refuses()
    {
        var (workspace, _, _, _) = await CreateAsync();
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ResolveWorkspaceRunDirectory(workspace, "missing-run"));
    }

    [Theory]
    [InlineData("subjects")]
    [InlineData("compliance")]
    public async Task Export_IncompleteDiscovery_RefusesPartialSelection(string area)
    {
        var (workspace, run, evidence, _) = await CreateAsync();
        var deep = Path.Combine(workspace, area);
        for (var i = 0; i < 10; i++) deep = Path.Combine(deep, "nested");
        Directory.CreateDirectory(deep);
        var destination = Path.Combine(_root.FullName, "aef");
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            if (area == "subjects") StoreV1Exporter.ResolveWorkspaceRunDirectory(workspace, evidence.Base.SourceRun.RunId);
            else StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination);
        });
        Assert.Contains("discovery is incomplete", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Export_Cancelled_DoesNotProduceOutput()
    {
        var (workspace, run, evidence, _) = await CreateAsync();
        var destination = Path.Combine(_root.FullName, "aef");
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination, ct: cancelled));
        Assert.Throws<OperationCanceledException>(() => StoreV1Exporter.ResolveWorkspaceRunDirectory(workspace, evidence.Base.SourceRun.RunId, cancelled));
        Assert.False(Directory.Exists(destination));
    }

    private static void ChangeJson(string file, Action<JsonNode> change)
    {
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        change(json);
        File.WriteAllText(file, json.ToJsonString());
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("layout")]
    public async Task Export_WindowsPathCasing_PreservesSameEvidence(string segment)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (workspace, run, _, _) = await CreateAsync();
        var source = segment == "subject" ? run.Replace("WorkspaceAgent", "workspaceagent", StringComparison.Ordinal)
            : run.Replace("subjects", "SUBJECTS", StringComparison.Ordinal).Replace("agents", "AGENTS", StringComparison.Ordinal)
                .Replace("runs", "RUNS", StringComparison.Ordinal);
        var destination = Path.Combine(_root.FullName, "aef");
        var stderr = new StringWriter();
        var exit = await AefCommand.RunExportAsync(source, destination, null, "mocked", "on", false, null, true, TextWriter.Null, stderr);
        Assert.True(exit == ExitCodes.Success, stderr.ToString());
        Assert.Equal(2, new AefComplianceArtifactReader().Read(destination).Documents.Count);
        Assert.Contains("E-compliance-original-1", File.ReadAllText(Path.Combine(destination, "results.ndjson")));
    }

    [Theory]
    [InlineData("missing-results")]
    [InlineData("manifest-count")]
    [InlineData("summary-count")]
    [InlineData("summary-id")]
    public async Task Export_InconsistentExecutionEvidence_RefusesBeforeOutput(string conflict)
    {
        var (workspace, run, _, _) = await CreateAsync();
        if (conflict == "missing-results") Directory.Delete(Path.Combine(run, "scenarios"), recursive: true);
        else if (conflict == "manifest-count") ChangeJson(Path.Combine(run, "manifest.json"), j => j["run"]!["scenarioCount"] = 2);
        else if (conflict == "summary-count") ChangeJson(Path.Combine(run, "summary.json"), j => j["stats"]!["total"] = 2);
        else ChangeJson(Path.Combine(run, "summary.json"), j => j["runId"] = "another-execution");
        var destination = Path.Combine(_root.FullName, "aef");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, run, destination));
        Assert.False(Directory.Exists(destination));
    }

    private static string[] SourceHashes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))
        .ToArray();

    private async Task<(string Workspace, string Run, GdprComplianceEvidence Evidence, byte[] Bytes)> CreateAsync()
    {
        var workspace = Path.Combine(_root.FullName, ".agenteval");
        var store = new FileSystemOutputStore(workspace);
        await store.InitializeSolutionAsync("fixture");
        var subject = new SubjectIdentity(SubjectKind.Agent, "WorkspaceAgent");
        var manifest = await store.StartRunAsync(subject, new RunContext("Fixture", "tests/fixture", "fixture", null, null, "benchmark"));
        var fixture = ComplianceRenderCommandTests.MakeFixtureEvidence(subject.Name);
        var scenario = EvalResultPersistence.ToScenarioResult(fixture.CompositeTree, "gdpr.art17.erasure", "Erasure", []);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);
        await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "PASS", new(1, 1, 0, 0), new Dictionary<string, double>()));
        var final = (await store.GetRunManifestAsync(manifest.Run.RunId))!;
        var evidence = fixture with { Base = fixture.Base with
        {
            SourceRun = new(final.Run.RunId, final.ContentHash),
            Controls = [new("gdpr.art17.erasure", "Erasure", "PASS", 0.9, [scenario.Id], null)],
        } };
        await store.SaveComplianceEvidenceAsync("GDPR", subject, evidence.Base);
        var directory = new FileSystemLayout(workspace).ComplianceTimestampDir("GDPR", subject, evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Json);
        File.WriteAllBytes(Path.Combine(directory, "gdpr-evidence.json"), bytes);
        return (workspace, store.ResolveRunDirectory(subject, manifest.Run.RunId), evidence, bytes);
    }

    private static string DuplicateRun(string workspace, string first, string runId)
    {
        var second = new FileSystemLayout(workspace).RunDir(new(SubjectKind.Agent, "OtherAgent"), runId);
        Directory.CreateDirectory(second);
        foreach (var file in Directory.GetFiles(first, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(second, Path.GetRelativePath(first, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var manifestFile = Path.Combine(second, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!;
        manifest["subject"]!["name"] = "OtherAgent";
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        return second;
    }
}
