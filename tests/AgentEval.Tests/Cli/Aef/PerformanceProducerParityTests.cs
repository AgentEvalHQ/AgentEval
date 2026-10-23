// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class PerformanceProducerParityTests
{
    [Theory]
    [InlineData("cost", true)]
    [InlineData("latency", true)]
    [InlineData("throughput", true)]
    [InlineData("latency", false)]
    public async Task Perf_ActualPreset_ConversionPreservesWholeBenchmarkComparison(string preset, bool capture)
    {
        using var temp = TempWorkspace.Create("PerfParity");
        var workspace = Path.Combine(temp.Path, ".agenteval");
        await new FileSystemOutputStore(workspace).InitializeSolutionAsync("perf-parity");
        var command = new Command("test");
        command.Options.Add(new Option<DirectoryInfo?>("--aef"));
        var controls = new AefCliOptions(command);
        var exit = await controls.InvokeAsync(command.Parse(["--aef-content", capture ? "on" : "off", "--legacy-output"]),
            () => BenchPerfCommand.RunAsync(preset, "subject", "workload", temp.Path, new MeasuredAgent()));
        Assert.True(exit is 0 or 9 or 10 or 11, "The actual offline command must finish with a domain outcome.");
        var nativePath = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(workspace, "aef"), "run.json", SearchOption.AllDirectories)))!;
        var legacyPath = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(workspace, "subjects"), "manifest.json", SearchOption.AllDirectories)))!;
        var convertedPath = Path.Combine(temp.Path, "converted");
        StoreV1Exporter.ExportWorkspaceRun(workspace, legacyPath, convertedPath,
            new() { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off });
        var reader = new AefComparisonReader();
        var native = reader.Read(nativePath);
        var converted = reader.Read(convertedPath);
        var original = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Assert.Single(
            Directory.GetFiles(Path.Combine(legacyPath, "scenarios"), "*.json"))))!;
        Assert.Equal((string?)original["name"], Assert.Single(converted.Scenarios).Name);
        var scenario = Assert.Single(native.Scenarios);
        Assert.Equal("perf-" + preset, scenario.Id);
        Assert.Equal(capture ? StimulusHash.Of("workload") : null, scenario.StimulusHash);
        Assert.Equal(native.Scenarios.Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement, s.StimulusHash, s.Comparability)),
            converted.Scenarios.Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement, s.StimulusHash, s.Comparability)));
        Assert.Equal(native.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal), converted.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(ComparisonVerdict.Comparable, native.CompareTo(converted).Verdict);
#if NET10_0_OR_GREATER
        var nativeView = await MissionControlProducerAssertions.ReadAsync(nativePath, (string?)original["name"]);
        var convertedView = await MissionControlProducerAssertions.ReadAsync(convertedPath, (string?)original["name"]);
        Assert.Equal(nativeView.Select(s => (s.Name, s.State, s.Score, s.Passed)), convertedView.Select(s => (s.Name, s.State, s.Score, s.Passed)));
#endif
    }

    private sealed class MeasuredAgent : IEvaluableAgent
    {
        public string Name => "offline-perf-agent";
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            // Bound the real throughput loop's call count; no network or provider is used.
            await Task.Delay(10, cancellationToken);
            return new() { Text = "observed answer" };
        }
    }
}
