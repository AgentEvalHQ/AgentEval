// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1RawFactTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("raw-facts");
    public void Dispose() => AefTestRuns.Delete(_out);

    [Theory]
    [InlineData("scenario.passed")]
    [InlineData("scenario.assertions.0.passed")]
    [InlineData("manifest.run.timestamp")]
    [InlineData("manifest.run.duration")]
    [InlineData("manifest.run.scenarioCount")]
    [InlineData("manifest.subject.kind")]
    [InlineData("manifest.solution.id")]
    [InlineData("manifest.git.dirty")]
    [InlineData("manifest.environment.ci")]
    [InlineData("summary.stats.total")]
    [InlineData("summary.stats.passed")]
    [InlineData("summary.stats.failed")]
    [InlineData("summary.stats.warnings")]
    [InlineData("summary.cost.estimatedCost")]
    [InlineData("summary.cost.promptTokens")]
    [InlineData("summary.cost.completionTokens")]
    public async Task ExportDirectory_MissingRecordedValueTypeFact_RefusesBeforePublishing(string member)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = Source(source, member.Split('.')[0]);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        if (member == "scenario.assertions.0.passed") root["assertions"]![0]!.AsObject().Remove("outcome");
        var parts = member.Split('.').Skip(1).ToArray();
        var parent = root;
        foreach (var part in parts[..^1]) parent = int.TryParse(part, out var index) ? parent[index]! : parent[part]!;
        Assert.True(parent.AsObject().Remove(parts[^1]));
        File.WriteAllText(path, root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportDirectory(source.RunDirectory, _out));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("scenario")]
    [InlineData("manifest")]
    [InlineData("summary")]
    public async Task ExportDirectory_DuplicateRawMembers_RefusesAmbiguousSource(string kind)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = Source(source, kind);
        var text = File.ReadAllText(path).TrimEnd();
        File.WriteAllText(path, text[..^1] + ",\"unfamiliar\":{\"value\":1,\"value\":2}}");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportDirectory(source.RunDirectory, _out));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("negative-duration")]
    [InlineData("unknown-kind")]
    [InlineData("negative-count")]
    [InlineData("missing-verdict")]
    public async Task ExportDirectory_InvalidRecordedFact_RefusesInsteadOfInventingAReplacement(string damage)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = Source(source, damage == "negative-count" ? "summary" : "manifest");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        if (damage == "negative-duration") json["run"]!["duration"] = "-00:00:01";
        if (damage == "unknown-kind") json["subject"]!["kind"] = 99;
        if (damage == "negative-count") json["stats"]!["passed"] = -1;
        if (damage == "missing-verdict") json["run"]!.AsObject().Remove("verdict");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportDirectory(source.RunDirectory, _out));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task ExportDirectory_BomAndOptionalHistoricalDefaults_PreservesValidLegacyInput()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var summaryPath = Source(source, "summary");
        var summary = JsonNode.Parse(File.ReadAllText(summaryPath))!;
        summary["stats"]!.AsObject().Remove("skipped");
        File.WriteAllText(summaryPath, summary.ToJsonString(), new UTF8Encoding(true));
        var scenarioPath = Source(source, "scenario");
        var scenario = JsonNode.Parse(File.ReadAllText(scenarioPath))!.AsObject();
        scenario.Remove("measurement");
        File.WriteAllText(scenarioPath, scenario.ToJsonString(), new UTF8Encoding(true));
        Assert.Equal(AefOutcome.Intact, StoreV1Exporter.ExportDirectory(source.RunDirectory, _out).Verification.Outcome);
    }

    [Fact]
    public async Task ExportAsync_FileSystemReader_MissingRecordedPassed_RefusesBeforePublishing()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = Source(source, "scenario");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("passed"));
        File.WriteAllText(path, json.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportAsync(source.Store, source.RunId, _out));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task ExportAsync_FileSystemReader_DuplicateRawMembers_RefusesBeforePublishing()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = Source(source, "scenario");
        var json = File.ReadAllText(path).TrimEnd();
        File.WriteAllText(path, json[..^1] + ",\"unfamiliar\":{\"value\":1,\"value\":2}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportAsync(source.Store, source.RunId, _out));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task ExportAsync_RootlessReader_PreservesRecordedTypedResult()
    {
        var store = new InMemoryOutputStore();
        Assert.Null(store.WorkspaceRoot);
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "typed"), new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId,
            new("case", "Case", "input", "output", true, 0.8, new Dictionary<string, double>(), [], TimeSpan.Zero, 0));
        await store.CompleteRunAsync(manifest,
            new("1.0", manifest.Run.RunId, "PASS", new(1, 1, 0, 0), new Dictionary<string, double>()));

        var converted = await StoreV1Exporter.ExportAsync(store, manifest.Run.RunId, _out);
        Assert.Equal(AefOutcome.Intact, converted.Verification.Outcome);
        Assert.Equal("passed", (string?)AefTestRuns.Results(_out).Single()["state"]);
    }

    private static string Source(StoreV1Fixture source, string kind) => kind == "scenario"
        ? Directory.GetFiles(Path.Combine(source.RunDirectory, "scenarios"), "*.json")
            .Single(p => (string?)JsonNode.Parse(File.ReadAllText(p))!["id"] == "refund-ok")
        : Path.Combine(source.RunDirectory, kind + ".json");
}
