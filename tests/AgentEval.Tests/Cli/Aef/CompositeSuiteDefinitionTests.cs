using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Tests.Cli.Aef;

public sealed class CompositeSuiteDefinitionTests
{
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
