// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Text.Json.Nodes;
using AgentEval.Benchmarks;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public sealed class ComparisonPerformanceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nidentity")]
    [InlineData("overlong")]
    public void Write_InvalidPresetIdentity_RefusesBeforeCreatingRun(string id)
    {
        using var temp = TempWorkspace.Create("InvalidPresetComparison");
        if (id == "overlong") id = new string('a', 257);
        var result = Result();
        var error = Assert.Throws<ArgumentException>(() => AefCompositeRunWriter.Write(temp.Path, result,
            AefCompositeRunWriter.Undescribed, Options(id), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.Contains("Expected:", error.Message);
        Assert.Contains("Actual:", error.Message);
        Assert.Contains("Suggestions:", error.Message);
        Assert.Contains("Because:", error.Message);
        Assert.Empty(Directory.GetFiles(temp.Path, "run.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_NestedRepeatedPreset_SelectsOnlyFinalAggregate(bool aborted)
    {
        using var temp = TempWorkspace.Create("RepeatedPresetComparison");
        var child = Result();
        var inner = child with { Metric = child.Metric with { Key = "inner" }, Details = child.Details with { SubResults = [child] } };
        var root = child with { Metric = child.Metric with { Key = "root" }, Details = child.Details with { SubResults = [inner] } };
        var trials = new[] { root, root };
        var rollup = StochasticBenchRunner.RollupTree(trials);
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var options = Options("whole-benchmark");
        var directory = aborted
            ? AefCompositeRunWriter.WriteAbortedTrials(temp.Path, trials, AefCompositeRunWriter.Undescribed,
                options with { AbortReason = "cancelled" }, start, start.AddSeconds(1)).Directory
            : AefCompositeRunWriter.WriteTrials(temp.Path, rollup, trials, AefCompositeRunWriter.Undescribed,
                options, start, start.AddSeconds(1)).Directory;
        var observation = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);
        Assert.Equal("whole-benchmark", observation.Id);
        Assert.Equal(aborted ? null : .9, observation.Score);
        Assert.Equal(9, File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Length);
    }

    private static EvalResult Result() => new(new("metric", "Metric", "test", "1.0"),
        new(.9, null, "pass", true, .6, "none", null), new(null, null, null, null, null),
        new("code", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private static AefCompositeRunOptions Options(string id) => new()
    {
        SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "suite:test",
        PresetComparisonId = id,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_PerformanceTree_SelectsOriginalBenchmarkAndRetainsSupportingMeasurements(bool capture)
    {
        using var temp = TempWorkspace.Create("PerfComparison");
        var child = new EvalResult(new("perf_latency", "Latency", "performance", "1.0"),
            new(.9, null, "pass", true, .6, "none", null), new(null, null, null, null, null),
            new("code", null, null, null, null, 0, false), DateTimeOffset.UtcNow);
        var result = child with
        {
            Metric = new("perf_benchmark", "Performance", "performance", "1.0"),
            Score = new(.4, null, "fail", false, .6, "high", null),
            Details = child.Details with { SubResults = [child], AggregationStrategy = "CapByWorst" },
            Observation = new("observed workload", null),
        };
        var command = new Command("test");
        command.Options.Add(new Option<DirectoryInfo?>("--aef"));
        var controls = new AefCliOptions(command);
        await controls.InvokeAsync(command.Parse(["--aef-content", capture ? "on" : "off"]), () =>
        {
            Assert.False(BenchAef.WriteResult(new DirectoryInfo(temp.Path), temp.Path, "perf", "Standard", "subject", null,
                AefTargetMode.Mocked, result, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow));
            return Task.FromResult(0);
        });
        var directory = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(temp.Path, "run.json", SearchOption.AllDirectories)))!;
        var run = new AefComparisonReader().Read(directory);
        var observation = Assert.Single(run.Scenarios);
        Assert.Equal("perf-standard", observation.Id);
        Assert.Equal(.4, observation.Score);
        Assert.Equal("perf_benchmark", observation.Comparability!.EvalKey);
        Assert.Equal(capture ? StimulusHash.Of("observed workload") : null, observation.StimulusHash);
        var lines = File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Select(x => JsonNode.Parse(x)!).ToList();
        Assert.Equal(2, lines.Count);
        var supporting = Assert.Single(lines, x => x["parentResultId"] is not null);
        Assert.Equal(.9, (double?)supporting["scores"]![0]!["value"]);
        Assert.False((bool)supporting["ext"]!["agenteval.comparison"]!["selected"]!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Evaluate_MetadataWorkload_ObservesActualPromptOrExplicitUnknownAndCodeInstrument(int count)
    {
        var agent = new RecordingAgent();
        var prompts = Enumerable.Range(0, count).Select(i => "actual-" + i).ToArray();
        AgentEval.Results.Suites.SuiteDefinitionManifest? definition = null;
        var result = await new PerformanceBenchmark(agent, new()
        {
            Verbose = false, DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new()
            {
                LatencyIterationsPerPrompt = 1, WarmupIterations = 0, ThroughputConcurrentRequests = 0,
                ThroughputDuration = TimeSpan.FromMilliseconds(1),
            },
        }).EvaluateAsync(new EvalInput("outer placeholder", Metadata: new Dictionary<string, object>
        {
            ["prompts"] = prompts,
            [PerformanceBenchmark.PlanPreparedMetadataKey] = (Action<PerformanceEvaluationPlan>)(plan =>
                definition = PerformanceSuiteDefinition.Create("standard", plan)),
        }));
        Assert.DoesNotContain("outer placeholder", agent.Prompts);
        Assert.NotNull(result.Observation);
        Assert.Equal(count == 1 ? prompts[0] : null, result.Observation.Query);
        var stored = EvalResultPersistence.ToScenarioResult(result, "perf-standard", "Performance", input: "outer placeholder");
        Assert.Equal(count == 1 ? StimulusHash.Of(prompts[0]) : null, stored.StimulusHash);
        Assert.Equal(EvaluationInstrumentKind.Deterministic, stored.Comparability!.EffectiveInstrumentKind);
        Assert.Contains("perf_benchmark", definition!.CaseIds);
    }

    private sealed class RecordingAgent : IEvaluableAgent
    {
        public string Name => "unpriced-test-agent";
        public List<string> Prompts { get; } = [];
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AgentResponse { Text = "answer" });
        }
    }
}
