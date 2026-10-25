// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;

namespace AgentEval.Tests.Cli;

public sealed class ComparisonInstrumentTests
{
    [Theory]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("threshold")]
    public void Persist_AggregateFactsNoLongerMatchResult_RefusesStaleAttribution(string field)
    {
        var trial = Trial();
        var rollup = StochasticBenchRunner.RollupTree([trial, trial]);
        var changed = field switch
        {
            "key" => rollup with { Metric = rollup.Metric with { Key = "another" } },
            "version" => rollup with { Metric = rollup.Metric with { Version = "2.0" } },
            _ => rollup with { Score = rollup.Score with { Threshold = .8 } },
        };
        Assert.Throws<ArgumentException>(() => Store(changed));
    }

    [Fact]
    public void AggregateComparability_EmptyTrials_RejectsWithActionableReason()
    {
        var error = Assert.Throws<ArgumentException>(() => EvalResultPersistence.AggregateComparability([]));
        Assert.Contains("Expected:", error.Message);
        Assert.Contains("Actual:", error.Message);
        Assert.Contains("Suggestions:", error.Message);
        Assert.Contains("Because:", error.Message);
    }

    [Fact]
    public void RubricWithoutNamedJudge_Endpoint_RefusesWithoutEchoing()
    {
        const string endpoint = "https://private.example.test/rubric";
        var error = Assert.Throws<ArgumentException>(() => new ComparabilityFacts("eval", "1.0") { RubricDigest = endpoint });
        Assert.DoesNotContain(endpoint, error.Message);
    }

    [Theory]
    [InlineData("atomic-llm", "LLM")]
    [InlineData("atomic-decision", "LLM")]
    [InlineData("custom", "OTHER")]
    public async Task Import_ExplicitInstrumentWithoutModel_PreservesItsKind(string type, string expected)
    {
        using var temp = TempWorkspace.Create("ImportInstrument");
        var scenario = Store(Trial(model: null, type: type));
        var source = await CompareCommandTests.WriteRunAsync(temp, "subject", [scenario]);
        var destination = Path.Combine(temp.Path, "imported");
        await StoreV1Exporter.ExportAsync(new FileSystemOutputStore(temp.Path), Path.GetFileName(source), destination);
        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(destination, "results.ndjson"))[0])!;
        Assert.Equal(expected, (string?)line["annotator"]?["kind"]);
        var projected = new AefComparisonReader().Read(destination).Scenarios.Single();
        Assert.Equal(scenario.Comparability!.EffectiveInstrumentKind, projected.Comparability!.EffectiveInstrumentKind);
        Assert.Equal(scenario.Comparability.EffectiveRubricDigest, projected.Comparability.EffectiveRubricDigest);
    }

    [Theory]
    [InlineData("rubric")]
    [InlineData("floor")]
    [InlineData("relation")]
    [InlineData("kind")]
    public void Rollup_PartiallyDisagreeingFacts_RetainsOnlyCommonEvidence(string difference)
    {
        var first = Trial();
        var second = difference switch
        {
            "rubric" => Trial(rubric: null),
            "floor" => first with { Details = first.Details with { Evidence = null } },
            "relation" => first with { Observation = new("prompt", "another-model") },
            _ => Trial(model: null, type: "atomic-code", rubric: null),
        };
        var facts = Store(StochasticBenchRunner.RollupTree([first, second])).Comparability!;
        Assert.Equal(difference == "kind" ? EvaluationInstrumentKind.Unknown : EvaluationInstrumentKind.Model, facts.EffectiveInstrumentKind);
        Assert.Equal(difference is "kind" or "rubric" ? null : first.Provenance.RubricDigest, facts.EffectiveRubricDigest);
        Assert.Equal(difference == "floor" ? null : .2, facts.ChanceFloor?.Bar);
        Assert.Equal(difference == "kind" ? null : "judge", facts.Judge?.ModelId);
        if (difference == "relation") Assert.Equal(JudgeSubjectRelation.Unknown, facts.Judge!.SubjectRelation);
    }

    [Theory]
    [InlineData("model", false)]
    [InlineData("rubric", false)]
    [InlineData("model", true)]
    [InlineData("rubric", true)]
    public void Compare_HomogeneousRollupsWithDifferentInstruments_Refuses(string difference, bool native)
    {
        using var temp = TempWorkspace.Create("RollupInstrument");
        var left = Trial();
        var right = Trial(model: difference == "model" ? "another-model" : "judge", rubric: difference == "rubric" ? "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var a = StochasticBenchRunner.RollupTree([left, left]);
        var b = StochasticBenchRunner.RollupTree([right, right]);
        var comparison = native
            ? new AefComparisonReader().Read(Write(temp.Path, a, [left, left])).CompareTo(new AefComparisonReader().Read(Write(temp.Path, b, [right, right])))
            : RunComparison.Of([Store(a)], [Store(b)]);
        Assert.Equal(ComparisonVerdict.Incomparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == (difference == "model" ? "judge.modelId" : "judge.rubricDigest"));
    }

    [Fact]
    public void Rollup_UnanimousInstrumentAndFloor_PreservesFactsAndSelfGrading()
    {
        using var temp = TempWorkspace.Create("RollupFloor");
        var trial = Trial();
        var rollup = StochasticBenchRunner.RollupTree([trial, trial]);
        Assert.Equal("composite", rollup.Provenance.Type);
        var legacy = Store(rollup);
        var native = new AefComparisonReader().Read(Write(temp.Path, rollup, [trial, trial])).Scenarios.Single();
        foreach (var observation in new[] { legacy, native })
        {
            Assert.Equal("judge", observation.Comparability!.Judge?.ModelId);
            Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", observation.Comparability.Judge?.RubricDigest);
            Assert.Equal(JudgeSubjectRelation.SameModel, observation.Comparability.Judge?.SubjectRelation);
            Assert.Equal(.2, observation.Comparability.ChanceFloor?.Bar);
            Assert.Equal("one of five equally likely choices", observation.Comparability.ChanceFloor?.Derivation);
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("threshold")]
    public void Rollup_IncompatibleTrialDefinitions_RefusesInsteadOfTakingFirst(string difference)
    {
        var first = Trial();
        var second = difference == "version" ? first with { Metric = first.Metric with { Version = "2.0" } }
            : first with { Score = first.Score with { Threshold = .8 } };
        Assert.Throws<ArgumentException>(() => StochasticBenchRunner.RollupTree([first, second]));
    }

    [Theory]
    [InlineData("atomic-llm")]
    [InlineData("atomic-decision")]
    public void Native_ModelGradingWithoutModelName_RecordsLlmAndRetainsRubric(string type)
    {
        using var temp = TempWorkspace.Create("UnnamedJudge");
        var a = Write(temp.Path, Trial(model: null, type: type));
        var line = JsonNode.Parse(File.ReadAllText(Path.Combine(a, "results.ndjson")))!;
        Assert.Equal("LLM", (string?)line["annotator"]?["kind"]);
        var b = Write(temp.Path, Trial(model: null, type: type, rubric: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var comparison = new AefComparisonReader().Read(a).CompareTo(new AefComparisonReader().Read(b));
        Assert.Equal(ComparisonVerdict.Incomparable, comparison.Verdict);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == "judge.rubricDigest");
    }

    [Fact]
    public void Compare_UnnamedModelGradingAndCode_AreDifferentInstruments()
    {
        var comparison = RunComparison.Of([Store(Trial(model: null))], [Store(Trial(model: null, type: "atomic-code", rubric: null))]);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == "judge");
    }

    [Fact]
    public void Compare_UnfamiliarProducerKind_ReportsUnknownInstrument()
    {
        var scenario = Store(Trial(model: null, type: "custom", rubric: null));
        var comparison = RunComparison.Of([scenario], [scenario]);
        Assert.Contains(comparison.Scenarios.Single().Unpinned, x => x.Name == "judge");
    }

    [Fact]
    public void Rollup_MixedJudgeNames_PreservesAgreedRubricAndFloorAsModelGraded()
    {
        var rollup = StochasticBenchRunner.RollupTree([Trial(), Trial(model: "another-model")]);
        var comparison = RunComparison.Of([Store(rollup)], [Store(Trial(model: null, type: "atomic-code", rubric: null))]);
        Assert.Contains(comparison.Scenarios.Single().Blocking, x => x.Name == "judge");
        Assert.Equal(.2, Store(rollup).Comparability!.ChanceFloor?.Bar);
    }

    private static ScenarioResult Store(EvalResult result) => EvalResultPersistence.ToScenarioResult(result, "case", "Case");

    private static EvalResult Trial(string? model = "judge", string? rubric = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string type = "atomic-llm") =>
        new(new("case", "Case", "test", "1.0"), new(.9, null, "pass", true, .7, "none", null),
            new(new Dictionary<string, double> { [ComparabilityFacts.ChanceFloorDimension] = .2 },
                [new(ComparabilityFacts.ChanceFloorEvidenceSource, "uniform", "one of five equally likely choices")], null, null, null),
            new(type, model, null, null, null, null, false) { RubricDigest = rubric }, DateTimeOffset.UtcNow)
        { Observation = new("prompt", "judge") };

    private static string Write(string root, EvalResult result, IReadOnlyList<EvalResult>? trials = null)
    {
        var options = new AefCompositeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "suite:test", ContentCapture = AefContentCapture.On };
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        return trials is null
            ? AefCompositeRunWriter.Write(root, result, AefCompositeRunWriter.Undescribed, options, start, start.AddSeconds(1)).Directory
            : AefCompositeRunWriter.WriteTrials(root, result, trials, AefCompositeRunWriter.Undescribed, options, start, start.AddSeconds(1)).Directory;
    }
}
