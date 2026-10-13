using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Agentic.StochasticStability;

namespace AgentEval.Tests.Cli.Aef;

public sealed class CompositeSuiteDefinitionTests
{
    private sealed class CyclicWrapper : IEvalDefinitionWrapper
    {
        public string Key => "cycle";
        public string Name => "cycle";
        public string Category => "test";
        public string Version => "1";
        public IEval DefinitionInner => this;
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Shape_CyclicWrapper_RefusesBoundedly()
    {
        Assert.Throws<ArgumentException>(() => AgentEval.Cli.Commands.BenchAef.Shape(new CyclicWrapper()));
    }
    [Theory]
    [InlineData("agentic-execution")]
    [InlineData("tool-call-accuracy")]
    [InlineData("rag-quality")]
    [InlineData("conversational")]
    [InlineData("reasoning")]
    [InlineData("user-experience")]
    [InlineData("telemetry")]
    [InlineData("stochastic-stability")]
    public void Capture_AgenticPreset_IdentifiesConfiguredInstrumentsWithoutCalls(string preset)
    {
        var judge = new Judge("private instrument");
        var graph = AgentEval.Cli.Commands.BenchAgenticCommand.ResolvePreset(preset, judge, "subject", "judge");
        var definition = CompositeSuiteDefinition.TryCreate("bench:agentic/" + preset, graph, new EvalInput("q", "r", GroundTruth: "secret reference"), 1);
        Assert.NotNull(definition);
        Assert.NotEmpty(definition.CaseIds);
        Assert.Equal(0, judge.Calls);
        Assert.DoesNotContain("secret reference", definition.ToJson().ToJsonString());
        var changed = CompositeSuiteDefinition.TryCreate("bench:agentic/" + preset, graph, new EvalInput("q", "r", GroundTruth: "different reference"), 1);
        Assert.NotEqual(definition.Digest, changed!.Digest);
    }
    [Fact]
    public void Capture_OperationalPolicies_BindSettingsButExcludeMeasurements()
    {
        var observations = new AgenticTelemetry(1, 2, 3, 4, 5, 6, 7, 8, new Dictionary<string, double> { ["private-tool"] = 9 });
        var changed = observations with { EstimatedCostUsd = 100, LatencyMsP99 = 999, TokensUsed = 999 };
        string Digest(IEval leaf)
        {
            var graph = new CompositeEval("root", "root", "test", "1", [new(leaf)], WeightedSumAggregation.Instance);
            var definition = CompositeSuiteDefinition.TryCreate("bench:operational", graph, new EvalInput("q", "r"), 1)!;
            Assert.NotNull(definition);
            Assert.DoesNotContain("private-tool", definition.ToJson().ToJsonString());
            return definition.Digest;
        }
        IEval[] baseline = [new CostEval(observations), new LatencyEval(observations), new TokenUsageEval(observations),
            new ToolLatencyEval(observations), new ErrorRateEval(observations), new RetryRateEval(observations), new StochasticStabilityEval()];
        IEval[] outcomes = [new CostEval(changed), new LatencyEval(changed), new TokenUsageEval(changed),
            new ToolLatencyEval(changed), new ErrorRateEval(changed), new RetryRateEval(changed), new StochasticStabilityEval()];
        IEval[] policies = [new CostEval(budgetUsd: 2), new LatencyEval(p99ThresholdMs: 20000), new TokenUsageEval(budgetTokens: 123),
            new ToolLatencyEval(perToolBudgetMs: 123), new ErrorRateEval(passThreshold: 0.9), new RetryRateEval(passThreshold: 0.91), new StochasticStabilityEval(0.9)];
        for (var i = 0; i < baseline.Length; i++)
        {
            Assert.Equal(Digest(baseline[i]), Digest(outcomes[i]));
            Assert.NotEqual(Digest(baseline[i]), Digest(policies[i]));
            var source = (IEvalConfigurationSource)baseline[i];
            var declaration = source.CaptureConfiguration();
            declaration.Clear();
            Assert.NotEmpty(source.CaptureConfiguration());
        }
        Assert.NotEqual(Digest(new LatencyEval()), Digest(new LatencyEval(p95ThresholdMs: 123)));
        Assert.NotEqual(Digest(new LatencyEval()), Digest(new LatencyEval(passThreshold: 0.9)));
        Assert.NotEqual(Digest(new CostEval()), Digest(new CostEval(passThreshold: 0.9)));
        Assert.NotEqual(Digest(new TokenUsageEval()), Digest(new TokenUsageEval(passThreshold: 0.9)));
    }

    private sealed class UnidentifiedJudge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => throw new InvalidOperationException("Capture must not execute a judge.");
    }
    private sealed class Subject(string name) : IEvaluableAgent
    {
        public string Name => name;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("Capture must not execute the subject.");
    }
    private sealed class Judge(string prompt) : IEvaluator, IJudgePromptSource, IJudgeProviderSource
    {
        public string? SystemPromptId => "fixture";
        public string PromptMaterial => prompt;
        public string? ProviderName => "fixture";
        public int Calls { get; private set; }
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationResult { OverallScore = output == "pass" ? 95 : 10 });
        }
    }
    private static CompositeEval Build(Judge judge, double threshold = 0.7, double weight = 1, string criterion = "secret criterion") =>
        new("root", "root", "test", "1", [new(new AtomicLlmEval(judge, "leaf", "leaf", "test", "1", [criterion], threshold, "judge"), weight)],
            WeightedSumAggregation.Instance, threshold);

    [Fact]
    public async Task Capture_ChangesOutcomes_RetainsDefinitionWithoutCallsOrText()
    {
        var judge = new Judge("secret prompt");
        var benchmark = Build(judge);
        var input = new EvalInput("secret query", "pass");
        var before = CompositeSuiteDefinition.TryCreate("bench:test", benchmark, input, 1)!;
        Assert.Equal(0, judge.Calls);
        await benchmark.EvaluateAsync(input);
        await benchmark.EvaluateAsync(input with { Response = "fail" });
        var after = CompositeSuiteDefinition.TryCreate("bench:test", benchmark, input with { Response = "fail" }, 1)!;
        Assert.Equal(before.Digest, after.Digest);
        Assert.Equal(new[] { "leaf" }, before.CaseIds);
        Assert.DoesNotContain("secret", before.ToJson().ToJsonString());
    }

    [Fact]
    public void Capture_InstrumentChanges_ChangesDefinition()
    {
        var input = new EvalInput("query", "response");
        string Digest(CompositeEval eval, int runs = 1) => CompositeSuiteDefinition.TryCreate("bench:test", eval, input, runs)!.Digest;
        var baseline = Digest(Build(new Judge("prompt")));
        Assert.NotEqual(baseline, Digest(Build(new Judge("new prompt"))));
        Assert.NotEqual(baseline, Digest(Build(new Judge("prompt"), threshold: 0.8)));
        Assert.NotEqual(baseline, Digest(Build(new Judge("prompt"), weight: 2)));
        Assert.NotEqual(baseline, Digest(Build(new Judge("prompt"), criterion: "changed")));
        Assert.NotEqual(baseline, Digest(Build(new Judge("prompt")), runs: 3));
        var declaredPolicy = CompositeSuiteDefinition.TryCreate("bench:test", Build(new Judge("prompt")),
            input with { SystemMessage = "private policy" }, 1)!;
        Assert.NotEqual(baseline, declaredPolicy.Digest);
        Assert.DoesNotContain("private policy", declaredPolicy.ToJson().ToJsonString());
    }

    [Fact]
    public void Capture_ScenarioPromptChanges_ChangesIdentityButSubjectDoesNot()
    {
        string Digest(string prompt, string subject)
        {
            var leaf = Build(new Judge("prompt")).Components[0].Eval;
            var graph = new CompositeEval("root", "root", "test", "1",
                [new(new AgentScenarioEval(new Subject(subject), prompt, leaf))], WeightedSumAggregation.Instance);
            return CompositeSuiteDefinition.TryCreate("bench:test", graph, new EvalInput("query", "response"), 1)!.Digest;
        }
        Assert.Equal(Digest("scenario", "one"), Digest("scenario", "two"));
        Assert.NotEqual(Digest("scenario", "one"), Digest("changed", "one"));
    }

    [Fact]
    public void Capture_UnknownPrompt_DoesNotClaimFullDefinition()
    {
        var graph = new CompositeEval("root", "root", "test", "1",
            [new(new AtomicLlmEval(new UnidentifiedJudge(), "leaf", "leaf", "test", "1", ["criterion"]))], WeightedSumAggregation.Instance);
        Assert.Null(CompositeSuiteDefinition.TryCreate("bench:test", graph, new EvalInput("query", "response"), 1));
    }
}
