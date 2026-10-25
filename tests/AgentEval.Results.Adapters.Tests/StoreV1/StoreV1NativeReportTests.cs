// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1NativeReportTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("native-report");
    private string Workspace => Path.Combine(_root, ".agenteval");
    private string Output => Path.Combine(_root, "converted");
    public void Dispose() => AefTestRuns.Delete(_root);
    private static byte[] Report() => JsonSerializer.SerializeToUtf8Bytes(new EvalResult(
        new("trace.test", "Trace test", "code", "1.0"), new(.9, null, "pass", true, .8, "none", null),
        new(new Dictionary<string, double> { ["USER_Key"] = 42 }, null, null, null, null),
        new("code", null, null, null, null, null, false), DateTimeOffset.Parse("2026-10-23T10:00:00Z")));

    private async Task<string> Source(byte[] bytes, string harness = "BenchTraceFidelityCommand")
    {
        var store = new FileSystemOutputStore(Workspace);
        await store.InitializeSolutionAsync("native-report");
        var subject = new SubjectIdentity(SubjectKind.Agent, "TraceAgent");
        var manifest = await store.StartRunAsync(subject, new("tests", ".", harness, null, null, "benchmark"));
        var directory = store.ResolveRunDirectory(subject, manifest.Run.RunId);
        await File.WriteAllBytesAsync(Path.Combine(directory, "report-native.json"), bytes);
        return directory;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_PascalCaseReport_PreservesFactsAndUnknownsWithoutChangingDictionaryKeys(bool capture)
    {
        var bytes = Report();
        var source = await Source(bytes);
        var conversion = StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output,
            new() { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off });
        Directory.Delete(Workspace, true);
        var run = new AefRunEvidenceReader().Read(Output);
        var row = Assert.Single(run.Results);
        Assert.Equal("trace.test", row.GetProperty("evaluator").GetProperty("id").GetString());
        Assert.False(row.TryGetProperty("durationMs", out _));
        Assert.False(row.GetProperty("ext").GetProperty(StoreV1Exporter.ExtName).TryGetProperty("stimulusHash", out _));
        Assert.Equal(42, row.GetProperty("scores").EnumerateArray().Single(s => s.GetProperty("metric").GetString() == "USER_Key").GetProperty("value").GetDouble());
        Assert.Contains(conversion.Notes, n => n.Contains("report-native.json", StringComparison.Ordinal));
        if (capture) Assert.Equal(bytes, run.CapturedContent("E-report-native-json")!.Value.ToArray());
        else Assert.Null(run.CapturedContent("E-report-native-json"));
    }

    [Theory]
    [InlineData("Value")]
    [InlineData("Passed")]
    [InlineData("CacheHit")]
    [InlineData("Metric")]
    [InlineData("children")]
    [InlineData("measurement")]
    [InlineData("domain")]
    public async Task Export_MalformedNativeReport_RefusesMissingOrInvalidFacts(string mutation)
    {
        var json = JsonNode.Parse(Report())!;
        if (mutation == "Metric") json.AsObject().Remove("Metric");
        else if (mutation == "CacheHit") json["Provenance"]!.AsObject().Remove(mutation);
        else if (mutation == "children") json["Details"]!["SubResults"] = new JsonObject();
        else if (mutation == "measurement") json["Score"]!["Measurement"] = 999;
        else if (mutation == "domain") json["Score"]!["Value"] = 2;
        else json["Score"]!.AsObject().Remove(mutation);
        var source = await Source(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output));
    }

    [Fact]
    public async Task Export_InvalidUtf8Report_RefusesReplacementCharacters()
    {
        var source = await Source([0xff]);
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output));
    }

    [Fact]
    public async Task Export_OversizedReport_RefusesBeforeReadingWholeJson()
    {
        var source = await Source(Report());
        using (var file = File.OpenWrite(Path.Combine(source, "report-native.json"))) file.SetLength((64L << 20) + 1);
        var error = Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output));
        Assert.Contains("64 MiB", error.Message);
    }

    [Fact]
    public async Task Export_OtherFamilyReport_PreservesOpaqueBytesWithoutInventingCases()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"OtherFamilyPayload\":true}");
        var source = await Source(bytes, "BenchMemoryCommand");
        StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output);
        var run = new AefRunEvidenceReader().Read(Output);
        Assert.Empty(run.Results);
        Assert.Equal(bytes, run.CapturedContent("E-report-native-json")!.Value.ToArray());
    }

    [Fact]
    public async Task Export_InterruptedTraceWithoutReport_DoesNotInventResults()
    {
        var source = await Source(Report());
        File.Delete(Path.Combine(source, "report-native.json"));
        StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output);
        var run = new AefRunEvidenceReader().Read(Output);
        Assert.Empty(run.Results);
        Assert.Equal("aborted", run.Info.Status);
        Assert.Null(run.CapturedContent("E-report-native-json"));
    }
}
