// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public sealed class ComparisonPolicyTests
{
    [Fact]
    public async Task Compare_OpaquePolicyDigestsDifferOnlyByCase_Refuses()
    {
        var left = WithPolicy(await Evaluate(), "opaque:AbC");
        var right = WithPolicy(left, "opaque:abc");
        Assert.Equal(ComparisonVerdict.Incomparable, RunComparison.Of([Store(left)], [Store(right)]).Verdict);
        Assert.Throws<ArgumentException>(() => StochasticBenchRunner.RollupTree([left, right]));
    }

    [Fact]
    public async Task Evaluate_NumericallyIdenticalPricingWithDifferentDecimalScale_PreservesPolicyIdentity()
    {
        var model = "policy-decimal-" + Guid.NewGuid().ToString("N");
        AgentEval.Models.ModelPricing.SetPricing(model, 1m, 2m);
        var first = await Evaluate();
        AgentEval.Models.ModelPricing.SetPricing(model, 1.000m, 2.000m);
        var second = await Evaluate();
        Assert.Equal(Store(first).Comparability!.EvaluationPolicyDigest, Store(second).Comparability!.EvaluationPolicyDigest);
    }

    [Fact]
    public async Task Import_PolicyEvidence_PreservesSharedFacts()
    {
        using var temp = TempWorkspace.Create("ImportPolicy");
        var scenario = Store(await Evaluate());
        var source = await CompareCommandTests.WriteRunAsync(temp, "subject", [scenario]);
        var destination = Path.Combine(temp.Path, "imported");
        await StoreV1Exporter.ExportAsync(new FileSystemOutputStore(temp.Path), Path.GetFileName(source), destination);
        var facts = new AefComparisonReader().Read(destination).Scenarios.Single().Comparability!;
        Assert.NotNull(facts.EvaluationPolicyDigest);
        Assert.Equal(scenario.Comparability!.EvaluationPolicyDigest, facts.EvaluationPolicyDigest);
    }

    [Fact]
    public async Task Compare_PolicyAbsentOnBothSides_ReportsBlindSpotAndStrictRefuses()
    {
        var result = WithPolicy(await Evaluate(), null);
        var comparison = RunComparison.Of([Store(result)], [Store(result)]);
        Assert.Equal(ComparisonVerdict.Comparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Unpinned, x => x.Name == "evaluationPolicyDigest");
        Assert.Equal(ComparisonVerdict.Incomparable, RunComparison.Of([Store(result)], [Store(result)], strict: true).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_OnlyOnePolicyRecorded_Refuses(bool missingBaseline)
    {
        var known = WithPolicy(await Evaluate(), "sha256:" + new string('a', 64));
        var missing = WithPolicy(known, null);
        var comparison = RunComparison.Of([Store(missingBaseline ? missing : known)], [Store(missingBaseline ? known : missing)]);
        Assert.Equal(ComparisonVerdict.Incomparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == "evaluationPolicyDigest");
    }

    [Fact]
    public async Task Rollup_OneTrialLacksPolicy_RefusesMixedEvidence()
    {
        var known = WithPolicy(await Evaluate(), "sha256:" + new string('a', 64));
        Assert.Throws<ArgumentException>(() => StochasticBenchRunner.RollupTree([known, WithPolicy(known, null)]));
    }

    [Fact]
    public async Task Rollup_UnanimousPolicy_PreservesEvidenceThroughBothProjections()
    {
        using var temp = TempWorkspace.Create("RollupPolicy");
        const string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = WithPolicy(await Evaluate(), digest);
        var rollup = StochasticBenchRunner.RollupTree([result, result]);
        foreach (var facts in new[] { Store(rollup).Comparability, Native(temp.Path, rollup).Scenarios.Single().Comparability })
            Assert.Equal(digest, (string?)JsonSerializer.SerializeToNode(facts)!["EvaluationPolicyDigest"]);
    }

    [Fact]
    public async Task Policy_Endpoint_RejectsWithoutEchoingSensitiveValue()
    {
        var result = await Evaluate();
        var error = Assert.Throws<ArgumentException>(() => WithPolicy(result, "https://private.example.test/policy"));
        Assert.DoesNotContain("private.example", error.Message);
    }

    private static EvalResult WithPolicy(EvalResult result, string? digest)
    {
        var json = JsonSerializer.SerializeToNode(Store(result).Comparability)!.AsObject();
        json["EvaluationPolicyDigest"] = digest;
        return result with { ComparisonObservation = json.Deserialize<ComparabilityFacts>()! };
    }

    [Theory]
    [InlineData("latency", false)]
    [InlineData("throughput", false)]
    [InlineData("cost", false)]
    [InlineData("latency", true)]
    [InlineData("throughput", true)]
    [InlineData("cost", true)]
    [InlineData("iterations", false)]
    [InlineData("warmup", false)]
    [InlineData("workers", false)]
    [InlineData("duration", false)]
    [InlineData("delay", false)]
    public async Task Compare_SamePromptDifferentScoringPolicy_Refuses(string setting, bool native)
    {
        using var temp = TempWorkspace.Create("PolicyComparison");
        var left = await Evaluate();
        var right = await Evaluate(setting);
        var comparison = native
            ? Native(temp.Path, left).CompareTo(Native(temp.Path, right))
            : RunComparison.Of([Store(left)], [Store(right)]);
        Assert.Equal(ComparisonVerdict.Incomparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == "evaluationPolicyDigest");
    }

    [Fact]
    public async Task Compare_SamePolicyDifferentSubject_RemainsComparable()
    {
        var left = await Evaluate(subject: "subject-a");
        var right = await Evaluate(subject: "subject-b");
        var comparison = RunComparison.Of([Store(left)], [Store(right)]);
        Assert.Equal(ComparisonVerdict.Comparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Axes,
            x => x.Name == "evaluationPolicyDigest" && x.State == ComparabilityAxisState.Match);
    }

    [Fact]
    public async Task Rollup_DifferentScoringPolicy_RefusesMixedInstrument()
    {
        var left = await Evaluate();
        var right = await Evaluate("cost");
        Assert.Throws<ArgumentException>(() => StochasticBenchRunner.RollupTree([left, right]));
    }

    private static ScenarioResult Store(EvalResult result) => EvalResultPersistence.ToScenarioResult(result, "perf-standard", "Performance");

    private static AefComparisonRun Native(string root, EvalResult result)
    {
        var now = DateTimeOffset.UtcNow;
        var directory = AefCompositeRunWriter.Write(root, result, AefCompositeRunWriter.Undescribed,
            new() { PresetComparisonId = "perf-standard", SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked,
                SuiteRef = "suite:test", Lane = "perf", ContentCapture = AefContentCapture.On }, now, now).Directory;
        return new AefComparisonReader().Read(directory);
    }

    private static Task<EvalResult> Evaluate(string? setting = null, string subject = "subject")
    {
        var options = new PerformanceBenchmarkEvaluateOptions
        {
            LatencyIterationsPerPrompt = 1, WarmupIterations = 0, ThroughputConcurrentRequests = 0,
            ThroughputDuration = TimeSpan.FromMilliseconds(1),
        };
        switch (setting)
        {
            case "latency": options.P99LatencyThresholdMs *= 2; break;
            case "throughput": options.MinThroughputRps *= 2; break;
            case "cost": options.MaxCostUSD *= 2; break;
            case "iterations": options.LatencyIterationsPerPrompt++; break;
            case "warmup": options.WarmupIterations++; break;
            case "workers": options.ThroughputConcurrentRequests++; break;
            case "duration": options.ThroughputDuration += TimeSpan.FromMilliseconds(1); break;
        }
        return new PerformanceBenchmark(new Agent(subject), new()
        {
            Verbose = false, DelayBetweenIterations = setting == "delay" ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero, EvaluateOptions = options,
        }).EvaluateAsync(new EvalInput("same prompt"));
    }

    private sealed class Agent(string name) : IEvaluableAgent
    {
        public string Name => name;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "answer" });
    }
}
