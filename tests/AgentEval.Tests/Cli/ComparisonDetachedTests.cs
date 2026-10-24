// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Tests.Output;
using static AgentEval.Tests.Cli.CompareCommandTests;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public sealed class ComparisonDetachedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_PresentSummaryMissingRequiredChildren_RefusesBothInputRoutes(bool copied)
    {
        using var temp = TempWorkspace.Create("InvalidSummary");
        var source = await WriteRunAsync(temp, "subject", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        File.WriteAllText(Path.Combine(source, "summary.json"), "{}");
        var destination = Path.Combine(temp.Path, "converted");
        if (copied)
            Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportDirectory(source, destination));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportAsync(
                new FileSystemOutputStore(temp.Path), Path.GetFileName(source), destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Compare_DuplicateRunIdUnderDifferentSubjects_ReadsTheSelectedFolders()
    {
        using var temp = TempWorkspace.Create("DuplicateRunCompare");
        var first = await WriteRunAsync(temp, "first", [Scenario("case", .5, true, "sha256:aaa", Facts())]);
        var second = await WriteRunAsync(temp, "second", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        var copy = Path.Combine(Path.GetDirectoryName(second)!, Path.GetFileName(first));
        Directory.Move(second, copy);
        var manifestPath = Path.Combine(copy, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["run"]!["runId"] = Path.GetFileName(first);
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var report = Capture(first, copy);
        Assert.Equal(ExitCodes.Success, report.Exit);
        var delta = JsonNode.Parse(report.Output)!["deltas"]![0]!;
        Assert.Equal(.5, (double)delta["baseline"]!);
        Assert.Equal(.8, (double)delta["candidate"]!);
    }

    [Fact]
    public async Task Compare_CopiedCanonicalLayoutWithoutSubjectMetadata_ReadsSelectedEvidence()
    {
        using var temp = TempWorkspace.Create("CanonicalCopyCompare");
        var source = await WriteRunAsync(temp, "subject", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        var copy = Path.Combine(temp.Path, "detached", "subjects", "agent", "copied", "runs", Path.GetFileName(source));
        Directory.CreateDirectory(Path.Combine(copy, "scenarios"));
        File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(copy, "manifest.json"));
        foreach (var file in Directory.GetFiles(Path.Combine(source, "scenarios")))
            File.Copy(file, Path.Combine(copy, "scenarios", Path.GetFileName(file)));
        Assert.Equal(ExitCodes.Success, Capture(source, copy).Exit);
    }

    [Fact]
    public async Task Export_WorkspaceManifestMissingRequiredChildren_RefusesBeforeDereferencing()
    {
        using var temp = TempWorkspace.Create("InvalidWorkspaceManifest");
        var source = await WriteRunAsync(temp, "subject", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");
        var destination = Path.Combine(temp.Path, "converted");
        await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportAsync(
            new FileSystemOutputStore(temp.Path), Path.GetFileName(source), destination));
        Assert.False(Directory.Exists(destination));
        Assert.NotEqual(ExitCodes.Success, Capture(source, source).Exit);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("missing-manifest")]
    [InlineData("scenarios")]
    [InlineData("arbitrary")]
    public async Task Compare_CopiedLegacyFiles_PreservesDeltasAndDisclosesImportClaims(string shape)
    {
        using var temp = TempWorkspace.Create("DetachedCompare");
        var source = await WriteRunAsync(temp, "subject", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        var detached = Directory.CreateDirectory(Path.Combine(temp.Path, "copied")).FullName;
        var destination = shape == "arbitrary" ? detached : Directory.CreateDirectory(Path.Combine(detached, "scenarios")).FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(source, "scenarios")))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        if (shape == "run") File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(detached, "manifest.json"));
        var input = shape == "scenarios" ? destination : detached;
        var before = Directory.GetFiles(detached, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var report = Capture(source, input);
        Assert.Equal(ExitCodes.Success, report.Exit);
        var json = JsonNode.Parse(report.Output)!;
        Assert.Equal(0, (double)json["deltas"]![0]!["delta"]!);
        var provenance = json["inputs"]!["candidate"]!;
        Assert.True((bool)provenance["imported"]!);
        var claims = provenance["asserted"]!.AsArray().Select(x => (string)x!).ToArray();
        Assert.Contains("execution.targetMode", claims);
        if (shape != "run")
        {
            Assert.Contains("startedAt", claims);
            Assert.Contains("subject", claims);
            Assert.Contains("runId", claims);
            Assert.Contains("abortReason", claims);
            Assert.Contains("unknown", (string)provenance["abortReason"]!);
            Assert.Contains("manifest", (string)provenance["abortReason"]!);
        }
        Assert.Equal(before.Count, Directory.GetFiles(detached, "*", SearchOption.AllDirectories).Length);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllText(pair.Key)));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("scenario")]
    [InlineData("null-scenario")]
    [InlineData("empty")]
    [InlineData("aef-marker")]
    [InlineData("incomplete-manifest")]
    [InlineData("null-manifest")]
    [InlineData("summary")]
    [InlineData("trace")]
    [InlineData("missing-id")]
    [InlineData("null-input")]
    [InlineData("null-output")]
    [InlineData("null-metrics")]
    [InlineData("null-assertions")]
    [InlineData("null-assertion")]
    public async Task Compare_DamagedDetachedInput_RefusesWithoutDeltas(string damage)
    {
        using var temp = TempWorkspace.Create("DamagedDetached");
        var source = await WriteRunAsync(temp, "subject", [Scenario("case", .8, true, "sha256:aaa", Facts())]);
        var detached = Directory.CreateDirectory(Path.Combine(temp.Path, "copied")).FullName;
        var scenarios = Directory.CreateDirectory(Path.Combine(detached, "scenarios")).FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(source, "scenarios")))
            if (damage != "empty") File.Copy(file, Path.Combine(scenarios, Path.GetFileName(file)));
        if (damage == "manifest") File.WriteAllText(Path.Combine(detached, "manifest.json"), "{");
        if (damage is "incomplete-manifest" or "null-manifest")
            File.WriteAllText(Path.Combine(detached, "manifest.json"), damage == "null-manifest" ? "null" : "{}");
        if (damage == "summary") File.WriteAllText(Path.Combine(detached, "summary.json"), "{");
        if (damage == "trace")
        {
            Directory.CreateDirectory(Path.Combine(detached, "traces"));
            File.WriteAllText(Path.Combine(detached, "traces", "agent-trace.json"), "{");
        }
        if (damage == "aef-marker") File.WriteAllText(Path.Combine(detached, "run.json"), "{");
        if (damage is "scenario" or "null-scenario")
            File.WriteAllText(Path.Combine(scenarios, "broken.json"), damage == "scenario" ? "{" : "null");
        if (damage is "missing-id" or "null-input" or "null-output" or "null-metrics" or "null-assertions" or "null-assertion")
        {
            var path = Directory.GetFiles(scenarios).Single();
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            if (damage == "missing-id") node.AsObject().Remove("id");
            else if (damage == "null-assertion") node["assertions"] = new JsonArray((JsonNode?)null);
            else node[damage[5..]] = null;
            File.WriteAllText(path, node.ToJsonString());
        }
        var report = Capture(source, detached);
        Assert.NotEqual(ExitCodes.Success, report.Exit);
        Assert.DoesNotContain("\"deltas\"", report.Output);
        Assert.NotEmpty(report.Error);
    }

    private static (int Exit, string Output, string Error) Capture(string baseline, string candidate)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return (CompareCommand.Run(baseline, candidate, asJson: true), output.ToString(), error.ToString());
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
}
