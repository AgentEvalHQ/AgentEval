using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Agentic.JudgeQuality;
using AgentEval.Evals.Agentic.Adversarial;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.Safety.Policy;
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
    [InlineData("judge-quality")]
    [InlineData("glass-box-diagnostics")]
    [InlineData("adversarial-direct")]
    [InlineData("safety")]
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

    [Fact]
    public void Capture_CalibrationReferences_BindsExpectedLabelsOnlyAndRefusesLazyInputs()
    {
        var graph = AgentEval.Cli.Commands.BenchAgenticCommand.ResolvePreset("judge-quality", new Judge("unused"));
        AgentEval.Results.Suites.SuiteDefinitionManifest? Capture(object pairs) =>
            CompositeSuiteDefinition.TryCreate("bench:judge-quality", graph,
                new EvalInput("q", "r", Metadata: new Dictionary<string, object> { ["calibration_pairs"] = pairs }), 1);
        var baseline = Capture(new[] { ("secret expected", "actual one") })!;
        Assert.Equal(baseline.Digest, Capture(new[] { ("secret expected", "actual two") })!.Digest);
        Assert.Equal(baseline.Digest, Capture("[{\"expected\":\"secret expected\",\"actual\":\"other\"}]")!.Digest);
        Assert.NotEqual(baseline.Digest, Capture(new[] { ("changed reference", "actual one") })!.Digest);
        Assert.NotEqual(baseline.Digest, Capture(Array.Empty<(string, string)>())!.Digest);
        Assert.DoesNotContain("secret expected", baseline.ToJson().ToJsonString());
        Assert.DoesNotContain("actual one", baseline.ToJson().ToJsonString());
        IEnumerable<(string, string)> Lazy()
        {
            throw new InvalidOperationException("Capture must not consume this source.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        Assert.Null(Capture(Lazy()));
        Assert.NotEqual(new JudgeDriftEval().CaptureConfiguration().ToJsonString(), new JudgeDriftEval(0.1).CaptureConfiguration().ToJsonString());
        Assert.NotEqual(new JudgeAgreementEval().CaptureConfiguration().ToJsonString(), new JudgeAgreementEval(0.9).CaptureConfiguration().ToJsonString());
        Assert.NotEqual(new CalibrationAccuracyEval().CaptureConfiguration().ToJsonString(), new CalibrationAccuracyEval(0.9).CaptureConfiguration().ToJsonString());
    }

    [Fact]
    public void Capture_Diagnostics_BindsBaselineJudgeAndRegexPolicyWithoutAddingCases()
    {
        string Digest(IEval leaf, string? baseline = null)
        {
            var graph = new CompositeEval("root", "root", "test", "1", [new(leaf)], WeightedSumAggregation.Instance);
            var metadata = new Dictionary<string, object>();
            if (baseline is not null) metadata["trusted_system_prompt"] = baseline;
            var manifest = CompositeSuiteDefinition.TryCreate("bench:diagnostics", graph, new EvalInput("q", "r", Metadata: metadata), 1)!;
            Assert.NotNull(manifest);
            Assert.Single(manifest.CaseIds);
            Assert.DoesNotContain("private baseline", manifest.ToJson().ToJsonString());
            return manifest.Digest;
        }
        var injection = new AgentEval.Evals.Agentic.Security.SystemPromptInjectionEval(new Judge("prompt"));
        Assert.NotEqual(Digest(injection), Digest(injection, ""));
        Assert.NotEqual(Digest(injection, "private baseline"), Digest(injection, "changed baseline"));
        Assert.NotEqual(Digest(injection), Digest(new AgentEval.Evals.Agentic.Security.SystemPromptInjectionEval(new Judge("changed"))));
        Assert.NotEqual(Digest(injection), Digest(new AgentEval.Evals.Agentic.Security.SystemPromptInjectionEval()));
        AgentEval.Evals.Agentic.Security.ArgumentSanitizationEval RegexEval(string pattern, int timeout) => new(
            new global::System.Text.RegularExpressions.Regex(pattern, global::System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(timeout)));
        Assert.NotEqual(Digest(RegexEval("secret", 100)), Digest(RegexEval("changed", 100)));
        Assert.NotEqual(Digest(RegexEval("secret", 100)), Digest(RegexEval("secret", 200)));
        var unknown = new AgentEval.Evals.Agentic.Security.ArgumentSanitizationEval(new global::System.Text.RegularExpressions.Regex("(?i)secret"));
        Assert.Null(unknown.CaptureConfiguration(new EvalInput("q", "r")));
    }

    private sealed class NestedInstrument(IEval instrument) : IEvalInstrumentSource
    {
        public string Key => "outer";
        public string Name => Key;
        public string Category => "test";
        public string Version => "1";
        public IReadOnlyList<IEval> DefinitionInstruments => [instrument];
        public global::System.Text.Json.Nodes.JsonObject CaptureConfiguration() => new();
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Capture_CompositeInstrument_DoesNotAddItsInternalCasesToEmittedRoster()
    {
        var graph = new CompositeEval("root", "root", "test", "1",
            [new(new NestedInstrument(Build(new Judge("prompt"))))], WeightedSumAggregation.Instance);
        var definition = CompositeSuiteDefinition.TryCreate("bench:nested", graph, new EvalInput("q", "r"), 1)!;
        Assert.Equal(new[] { "outer" }, definition.CaseIds);
        Assert.Contains("leaf", definition.ToJson().ToJsonString());
    }

    [Fact]
    public void Capture_AdversarialPolicy_BindsCompiledPatternsThresholdAndLimit()
    {
        var judge = new Judge("private judge");
        string Digest(IEval eval)
        {
            var graph = new CompositeEval("root", "root", "test", "1", [new(eval)], WeightedSumAggregation.Instance);
            var definition = CompositeSuiteDefinition.TryCreate("bench:adversarial", graph, new EvalInput("q", "r"), 1)!;
            Assert.Single(definition.CaseIds);
            Assert.DoesNotContain("secret-pattern", definition.ToJson().ToJsonString());
            return definition.Digest;
        }
        var direct = Digest(new DirectInjectionEval(judge));
        Assert.NotEqual(direct, Digest(new DirectInjectionEval(judge, customPatterns: ["secret-pattern"])));
        Assert.Equal(direct, Digest(new DirectInjectionEval(judge, customPatterns: ["["]))); // rejected pattern never executes
        Assert.NotEqual(direct, Digest(new DirectInjectionEval(judge, passThreshold: 0.8)));
        Assert.NotEqual(Digest(new PersonaAttackEval(judge)), Digest(new PersonaAttackEval(judge, customPatterns: ["secret-pattern"])));
        Assert.NotEqual(Digest(new JailbreakResistanceEval(judge)), Digest(new JailbreakResistanceEval(judge, patternsToRun: 2)));
        Assert.NotEqual(Digest(new JailbreakResistanceEval(judge)), Digest(new JailbreakResistanceEval(judge, passThreshold: 0.8)));
        var overrideMatcher = new DirectInjectionEval(judge) { RegexMatch = (_, _) => throw new InvalidOperationException() };
        Assert.Null(overrideMatcher.CaptureConfiguration(new EvalInput("q", "r")));
        Assert.Equal(0, judge.Calls);
    }

    private sealed class ExternalClassifier : IContentSafetyClient
    {
        public Task<IReadOnlyDictionary<string, double>> ClassifyAsync(string content, CancellationToken ct = default)
            => throw new InvalidOperationException("Capture must not call a classifier.");
    }

    [Fact]
    public void Capture_ExternalSafetyClassifier_DeclinesWithoutCallingIt()
    {
        var judge = new Judge("prompt");
        IEvalConfigurationSource[] leaves = [new HateUnfairnessEval(judge, new ExternalClassifier()),
            new SelfHarmEval(judge, new ExternalClassifier()), new SexualEval(judge, new ExternalClassifier()),
            new ViolenceEval(judge, new ExternalClassifier())];
        Assert.All(leaves, leaf => Assert.Null(leaf.CaptureConfiguration(new EvalInput("q", "r"))));
        Assert.NotNull(new HateUnfairnessEval(judge, NullContentSafetyClient.Instance).CaptureConfiguration(new EvalInput("q", "r")));
    }

    private sealed class DynamicPolicy : IPolicyResolver
    {
        public ProhibitedActionPolicy GetPolicyFor(string subjectId) => throw new InvalidOperationException("Capture must not resolve a dynamic policy.");
    }

    [Fact]
    public void Capture_SafetyPolicy_BindsPolicyAndPatternsWithoutSubjectIdentityOrDynamicCalls()
    {
        var judge = new Judge("private judge");
        string Digest(IEval eval)
        {
            var graph = new CompositeEval("root", "root", "test", "1", [new(eval)], WeightedSumAggregation.Instance);
            var definition = CompositeSuiteDefinition.TryCreate("bench:safety", graph, new EvalInput("q", "r"), 1)!;
            Assert.DoesNotContain("private policy", definition.ToJson().ToJsonString());
            return definition.Digest;
        }
        var policy = new StaticPolicyResolver(new ProhibitedActionPolicy(["private policy"], [], [], []));
        var baseline = Digest(new ProhibitedActionsEval(judge, policy, "subject one"));
        Assert.Equal(baseline, Digest(new ProhibitedActionsEval(judge, policy, "subject two")));
        Assert.NotEqual(baseline, Digest(new ProhibitedActionsEval(judge,
            new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], [])), "subject one")));
        Assert.NotEqual(baseline, Digest(new ProhibitedActionsEval(judge, policy, "subject one", passThreshold: 0.8)));
        Assert.Null(new ProhibitedActionsEval(judge, new DynamicPolicy(), "subject").CaptureConfiguration(new EvalInput("q", "r")));
        Assert.NotEqual(Digest(new SensitiveDataLeakageEval(judge)), Digest(new SensitiveDataLeakageEval(judge, customPatterns: ["private policy"])));
        Assert.NotEqual(Digest(new SystemPromptLeakageEval(judge)), Digest(new SystemPromptLeakageEval(judge, passThreshold: 0.8)));
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public void Capture_BuiltInMockJudge_DeclaresFixedImplementationWithoutInventingAPrompt()
    {
        var judge = new AgentEval.Cli.Commands.MockTarget.Judge();
        var graph = AgentEval.Cli.Commands.BenchAgenticCommand.ResolvePreset("rag-quality", judge);
        var manifest = CompositeSuiteDefinition.TryCreate("bench:agentic/rag-quality", graph, new EvalInput("q", "r"), 1)!;
        Assert.NotNull(manifest);
        Assert.Contains("fixed-placeholder", manifest.ToJson().ToJsonString());
        Assert.DoesNotContain("prompt-identified", manifest.ToJson().ToJsonString());
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
