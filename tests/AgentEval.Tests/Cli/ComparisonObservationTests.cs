// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;

namespace AgentEval.Tests.Cli;

public sealed class ComparisonObservationTests
{
    private const string Outer = "(each scenario sends its own prompt to the live agent)";

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("eu-ai-act", false)]
    [InlineData("agentic", false)]
    [InlineData("rollup", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", true)]
    [InlineData("agentic", true)]
    [InlineData("rollup", true)]
    public async Task Persist_BenchmarkRunners_PreserveObservedFactsAndExplicitUnknowns(string runner, bool observed)
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "test");
        var input = new EvalInput(Outer, "placeholder") { SubjectModel = "actual-model" };
        IEval eval = observed ? new AgentScenarioEval(new Subject(), "actual prompt", Judge()) : new UnknownEval();
        var benchmark = new CompositeEval("suite", "Suite", "test", "1.0", [new(eval)], WeightedSumAggregation.Instance);
        var runId = runner switch
        {
            "gdpr" => (await new AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner().RunAsync(store, subject, benchmark, input)).RunId,
            "eu-ai-act" => (await new AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner().RunAsync(store, subject, benchmark, input)).RunId,
            "agentic" => (await new AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner().RunAsync(store, subject, benchmark, input)).RunId,
            _ => await StochasticBenchRunner.PersistRollupAsync(store, subject, await eval.EvaluateAsync(input), input, default),
        };
        var stored = new List<ScenarioResult>();
        await foreach (var scenario in store.GetScenarioResultsAsync(runId)) stored.Add(scenario);
        var result = Assert.Single(stored);
        Assert.Equal(observed ? StimulusHash.Of("actual prompt") : null, result.StimulusHash);
        Assert.Equal(observed ? "actual prompt" : "", result.Input);
        Assert.Equal(observed ? JudgeSubjectRelation.SameModel : JudgeSubjectRelation.Unknown, result.Comparability!.Judge!.SubjectRelation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persist_DrivenScenario_UsesActualInnermostStimulusAndReportedModel(bool nested)
    {
        IEval eval = new AgentScenarioEval(new Subject(), "actual\r\nprompt", Judge());
        if (nested) eval = new AgentScenarioEval(new Subject(), "intermediate prompt", eval);
        var result = await eval.EvaluateAsync(new EvalInput(Outer, "placeholder") { SubjectModel = "declared-model" });
        var stored = EvalResultPersistence.ToScenarioResult(result, "case", "Case", input: Outer, subjectModel: "declared-model");

        Assert.Equal(StimulusHash.Of("actual\nprompt"), stored.StimulusHash);
        Assert.Equal("actual\r\nprompt", stored.Input);
        Assert.Equal(JudgeSubjectRelation.SameModel, stored.Comparability!.Judge!.SubjectRelation);
    }

    [Theory]
    [InlineData(AefContentCapture.On)]
    [InlineData(AefContentCapture.Off)]
    public async Task Native_DrivenScenario_RecordsObservationSubjectToCapturePolicy(AefContentCapture capture)
    {
        using var temp = TempWorkspace.Create("ObservedNative");
        var result = await new AgentScenarioEval(new Subject(), "secret scenario prompt", Judge())
            .EvaluateAsync(new EvalInput(Outer, "placeholder"));
        var directory = Native(temp.Path, result, capture);
        var observation = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);

        Assert.Equal(capture == AefContentCapture.On ? StimulusHash.Of("secret scenario prompt") : null, observation.StimulusHash);
        Assert.Equal(JudgeSubjectRelation.SameModel, observation.Comparability!.Judge!.SubjectRelation);
        Assert.DoesNotContain("secret scenario prompt", File.ReadAllText(Path.Combine(directory, "results.ndjson")));
    }

    [Fact]
    public async Task Compare_DifferentActualScenarioPrompts_RefusesDespiteIdenticalOuterPlaceholder()
    {
        using var temp = TempWorkspace.Create("ObservedDifferent");
        var reader = new AefComparisonReader();
        async Task<AefComparisonRun> Run(string prompt) => reader.Read(Native(temp.Path,
            await new AgentScenarioEval(new Subject(), prompt, Judge()).EvaluateAsync(new EvalInput(Outer, "placeholder")),
            AefContentCapture.On));
        var baseline = await Run("delete data");
        var candidate = await Run("export data");
        Assert.Equal(ComparisonVerdict.Incomparable, baseline.CompareTo(candidate).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollup_Trials_OnlyRetainsAgreedStimulus(bool different)
    {
        async Task<EvalResult> Trial(string prompt) => await new AgentScenarioEval(new Subject(), prompt, Judge())
            .EvaluateAsync(new EvalInput(Outer, "placeholder"));
        var rollup = StochasticBenchRunner.RollupTree([await Trial("actual"), await Trial(different ? "different" : "actual")]);
        var scenario = EvalResultPersistence.ToScenarioResult(rollup, "case", "Case", input: Outer);
        Assert.Equal(different ? null : StimulusHash.Of("actual"), scenario.StimulusHash);
    }

    [Fact]
    public async Task Persist_CodeEval_UsesObservedInputWithoutOuterArguments()
    {
        var result = await new CodeEval().EvaluateAsync(new EvalInput("code prompt", "answer"));
        var scenario = EvalResultPersistence.ToScenarioResult(result, "case", "Case");
        Assert.Equal(StimulusHash.Of("code prompt"), scenario.StimulusHash);
    }

    private static AtomicLlmEval Judge() => new(new JudgeStub(), "observed", "Observed", "test", "1.0", ["accuracy"], judgeModel: "actual-model");

    private static string Native(string root, EvalResult result, AefContentCapture capture)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        return AefCompositeRunWriter.Write(root, result, AefCompositeRunWriter.Undescribed,
            new AefCompositeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "suite:test", ContentCapture = capture },
            start, start.AddSeconds(1)).Directory;
    }

    private sealed class Subject : IEvaluableAgent
    {
        public string Name => "test";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "answer", ModelId = "actual-model" });
    }

    private sealed class JudgeStub : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 90 });
    }

    private sealed class CodeEval() : AtomicCodeEval("code", "Code", "test", "1.0")
    {
        protected override EvalResult Evaluate(EvalInput input) => Build(1, true, "none");
    }

    private sealed class UnknownEval() : AtomicEval("custom", "Custom", "test", "1.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(new(Key, Name, Category, Version), new(1, null, "pass", true, .7, "none", null),
                new(null, null, null, null, null), new("atomic-llm", "actual-model", null, null, null, null, false), DateTimeOffset.UtcNow));
    }
}
