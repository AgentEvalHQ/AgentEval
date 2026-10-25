// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1MemoryReportTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("memory-report-only");
    private string Workspace => Path.Combine(_root, ".agenteval");
    private string Output => Path.Combine(_root, "converted");
    public void Dispose() => AefTestRuns.Delete(_root);

    private static byte[] Report(bool typed = false) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["BenchmarkId"] = typed ? "typedmemeval-episodic" : "longmemeval", ["SelectedQuestions"] = 2,
        ["QuestionResults"] = new JsonArray(new JsonObject { ["QuestionId"] = "a" }, new JsonObject { ["QuestionId"] = "b" }),
        ["FutureProducerFact"] = "preserve these exact original bytes",
    }.ToJsonString());

    private async Task<string> Source(byte[] bytes, string harness = "BenchLongMemEvalCommand", int count = 2, bool scenario = false, bool complete = true)
    {
        var store = new FileSystemOutputStore(Workspace);
        await store.InitializeSolutionAsync("memory-report-only");
        var subject = new SubjectIdentity(SubjectKind.Agent, "MemoryAgent");
        var manifest = await store.StartRunAsync(subject, new("tests", ".", harness, null, null, "benchmark"));
        if (scenario) await store.WriteScenarioResultAsync(manifest.Run.RunId,
            new ScenarioResult("case", "Case", "", "", true, 1, new Dictionary<string, double>(), [], null, null));
        if (complete) await store.CompleteRunAsync(manifest,
            new("1.0", manifest.Run.RunId, "WARN", new(count, count, 0, 0, 0), new Dictionary<string, double> { ["selected"] = count }));
        var directory = store.ResolveRunDirectory(subject, manifest.Run.RunId);
        await File.WriteAllBytesAsync(Path.Combine(directory, "report-native.json"), bytes);
        return directory;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Export_KnownReportOnlyProducer_PreservesOriginalCountWithoutInventingCases(bool typed, bool capture, bool workspace)
    {
        var bytes = Report(typed);
        var source = await Source(bytes, typed ? "BenchTypedMemEvalCommand" : "BenchLongMemEvalCommand");
        var options = new StoreV1ExportOptions { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off };
        var conversion = workspace ? StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output, options)
            : StoreV1Exporter.ExportDirectory(source, Output, options);
        AefTestRuns.Delete(Workspace);
        var result = new AefRunEvidenceReader().Read(Output);
        Assert.True(result.Info.Imported);
        Assert.Equal("completed", result.Info.Status);
        Assert.Empty(result.Results);
        Assert.Empty(result.Gates);
        Assert.Equal(2, result.Header.GetProperty("ext").GetProperty("agenteval.store-v1").GetProperty("run").GetProperty("scenarioCount").GetInt32());
        Assert.Equal(2, result.Summary!.Value.GetProperty("ext").GetProperty("agenteval.store-v1").GetProperty("stats").GetProperty("total").GetInt32());
        Assert.Contains(conversion.Notes, note => note.Contains("2 questions", StringComparison.Ordinal) && note.Contains("no canonical evaluator results", StringComparison.Ordinal));
        if (capture) Assert.Equal(bytes, result.CapturedContent("E-report-native-json")!.Value.ToArray());
        else Assert.Null(result.CapturedContent("E-report-native-json"));
        var error = Assert.Throws<InvalidDataException>(() => new AefComparisonReader().Read(Output));
        Assert.Contains("no final comparison results", error.Message);
    }

    [Theory]
    [InlineData("benchmark")]
    [InlineData("missing-benchmark")]
    [InlineData("selected-count")]
    [InlineData("selected-type")]
    [InlineData("missing-selected")]
    [InlineData("question-array")]
    [InlineData("question-object")]
    [InlineData("question-id")]
    [InlineData("missing-report")]
    [InlineData("utf8")]
    [InlineData("duplicate-property")]
    [InlineData("top-array")]
    public async Task Export_InvalidReportClassification_RefusesBeforeWriting(string fault)
    {
        var document = JsonNode.Parse(Report())!.AsObject();
        switch (fault)
        {
            case "benchmark": document["BenchmarkId"] = "typedmemeval-episodic"; break;
            case "missing-benchmark": document.Remove("BenchmarkId"); break;
            case "selected-count": document["SelectedQuestions"] = 3; break;
            case "selected-type": document["SelectedQuestions"] = "2"; break;
            case "missing-selected": document.Remove("SelectedQuestions"); break;
            case "question-array": document["QuestionResults"] = new JsonObject(); break;
            case "question-object": document["QuestionResults"]![0] = "question"; break;
            case "question-id": document["QuestionResults"]![0]!["QuestionId"] = " "; break;
        }
        var bytes = fault switch
        {
            "utf8" => new byte[] { 0xff },
            "duplicate-property" => Encoding.UTF8.GetBytes("{\"BenchmarkId\":\"longmemeval\",\"BenchmarkId\":\"other\"}"),
            "top-array" => Encoding.UTF8.GetBytes("[]"),
            _ => Encoding.UTF8.GetBytes(document.ToJsonString()),
        };
        var source = await Source(bytes);
        if (fault == "missing-report") File.Delete(Path.Combine(source, "report-native.json"));
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output));
        Assert.False(Directory.Exists(Output));
    }

    [Theory]
    [InlineData("ordinary-count")]
    [InlineData("report-count")]
    [InlineData("unknown-producer")]
    [InlineData("typed-empty-family")]
    public async Task Export_ConflictingOrUnknownCountUnit_DoesNotRelaxScenarioGuard(string fault)
    {
        var document = JsonNode.Parse(Report())!;
        if (fault == "typed-empty-family") document["BenchmarkId"] = "typedmemeval-";
        var harness = fault == "unknown-producer" ? "UnknownMemoryCommand"
            : fault == "typed-empty-family" ? "BenchTypedMemEvalCommand" : "BenchLongMemEvalCommand";
        var source = await Source(Encoding.UTF8.GetBytes(document.ToJsonString()), harness,
            count: fault == "report-count" ? 3 : 2, scenario: fault == "ordinary-count");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task Export_PartialReport_DoesNotInferCompletionOrCanonicalCases()
    {
        var source = await Source(Report(), complete: false);
        StoreV1Exporter.ExportWorkspaceRun(Workspace, source, Output);
        var result = new AefRunEvidenceReader().Read(Output);
        Assert.Equal("aborted", result.Info.Status);
        Assert.Empty(result.Results);
        Assert.Empty(result.Gates);
        Assert.Null(result.EndedAt);
    }
}
