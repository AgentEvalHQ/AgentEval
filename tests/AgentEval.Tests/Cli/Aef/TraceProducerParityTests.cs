// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Tracing;
using AgentEval.Tests.Output;
using System.Text.Json;
using AgentEval.Results.Writing;
using AgentEval.Results.Adapters.Comparison;
#if NET10_0_OR_GREATER
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
#endif

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class TraceProducerParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TraceBench_RealCompletedLegacyRun_ConversionRetainsRecordedEvaluatorResults(bool workflow, bool capture)
    {
        using var temp = TempWorkspace.Create("TraceParity");
        var workspace = Path.Combine(temp.Path, ".agenteval");
        await new FileSystemOutputStore(workspace).InitializeSolutionAsync("trace-parity");
        var trace = Path.Combine(temp.Path, "trace.json");
        if (workflow) await WorkflowTraceSerializer.SaveToFileAsync(new WorkflowTrace
        {
            TraceName = "workflow", OriginalPrompt = "go", FinalOutput = "done",
            Steps = { new WorkflowTraceStep { ExecutorId = "a", Output = "done", StepIndex = 0 } },
        }, trace);
        else await TraceSerializer.SaveToFileAsync(new AgentEval.Tracing.AgentTrace(), trace);
        var nativeDirectory = Path.Combine(temp.Path, "native");
        var exit = await NativeVerbIntegrationTests.WithCapture(capture, () => workflow
            ? BenchWorkflowTraceFidelityCommand.RunAsync(trace, "standard", "subject", temp.Path, aef: new(nativeDirectory))
            : BenchTraceFidelityCommand.RunAsync(trace, trace, "standard", "subject", temp.Path, aef: new(nativeDirectory)));
        Assert.True(exit is 0 or 9 or 10 or 11, "The actual offline benchmark must finish with a domain outcome, not setup/IO failure.");
        var nativePath = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(nativeDirectory, "run.json", SearchOption.AllDirectories)))!;
        var native = new AefRunEvidenceReader().Read(nativePath);
        var legacy = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(workspace, "subjects"), "report-native.json", SearchOption.AllDirectories)))!;
        var original = File.ReadAllBytes(Path.Combine(legacy, "report-native.json"));
        NativeReportAssertions.MatchesProducer(nativePath, legacy, capture);
        Assert.NotEmpty(native.Results);
        var output = Path.Combine(temp.Path, "converted");
        StoreV1Exporter.ExportWorkspaceRun(workspace, legacy, output, new() { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off });
        var converted = new AefRunEvidenceReader().Read(output);
        Assert.Equal(native.Results.Select(Observation).OrderBy(x => x.Id, StringComparer.Ordinal),
            converted.Results.Select(Observation).OrderBy(x => x.Id, StringComparer.Ordinal));
        Assert.All(converted.Results, r => Assert.False(r.TryGetProperty("durationMs", out _)));
        if (capture) Assert.Equal(original, converted.CapturedContent("E-report-native-json")!.Value.ToArray());
        else Assert.Null(converted.CapturedContent("E-report-native-json"));
        var nativeComparison = new AefComparisonReader().Read(nativePath);
        var convertedComparison = new AefComparisonReader().Read(output);
        Assert.Equal(nativeComparison.Scenarios.Select(s => (s.Id, s.Score, s.Measurement)).OrderBy(s => s.Id, StringComparer.Ordinal),
            convertedComparison.Scenarios.Select(s => (s.Id, s.Score, s.Measurement)).OrderBy(s => s.Id, StringComparer.Ordinal));
        Assert.Equal(nativeComparison.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal),
            convertedComparison.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal));
#if NET10_0_OR_GREATER
        var source = new WorkspaceRunSource(new FileSystemOutputStore(workspace), new AefWorkspaceEvidenceReader());
        var view = await new Query().Run(source, converted.Info.RunId);
        Assert.NotNull(view);
        Assert.Equal(native.Results.Count(r => !r.TryGetProperty("parentResultId", out _)), view.Run.ScenarioCount);
#endif
    }

    private static (string? Id, string? State, double? Score) Observation(JsonElement result)
    {
        var score = result.TryGetProperty("scores", out var scores)
            ? scores.EnumerateArray().SingleOrDefault(s => s.GetProperty("metric").GetString() == "score") : default;
        return (result.GetProperty("evaluator").GetProperty("id").GetString(), result.GetProperty("state").GetString(),
            score.ValueKind == JsonValueKind.Object ? score.GetProperty("value").GetDouble() : null);
    }
}
