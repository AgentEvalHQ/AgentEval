// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// A result that produced no verdict is not a FAIL (#203 review, B9b). The agentic, GDPR and EU AI Act reports and run
/// summaries — and the OWASP / MITRE / NIST / perf commands — mapped every label but pass and warn to FAIL, so a judge
/// that answered off its rubric's scale read as an agent that failed: "FAIL 0%", "Review failures in …", exit 9, and a
/// run with nothing measured read "FAIL (score 100%)".
/// </summary>
public class ReportStatusTests
{
    private static EvalScore Score(string label, bool passed = false, MeasurementState state = MeasurementState.Measured) =>
        new(passed ? 1.0 : 0.0, null, label, passed, null, "none", null) { Measurement = state };

    [Theory]
    [InlineData("pass", true, "PASS")]
    [InlineData("warn", false, "WARN")]
    [InlineData("fail", false, "FAIL")]
    [InlineData("error", false, "ERROR")]
    [InlineData("skipped", false, "SKIPPED")]
    [InlineData("inapplicable", false, "SKIPPED")]
    [InlineData("PASS", true, "PASS")]          // case-insensitive
    public void ReportStatus_KeepsANonVerdictApartFromAFailure(string label, bool passed, string status)
    {
        Assert.Equal(status, Score(label, passed).ReportStatus());
    }

    [Theory]
    [InlineData("pass", 1, 0, 0, "PASS")]
    [InlineData("warn", 1, 0, 1, "WARN")]
    [InlineData("fail", 0, 1, 0, "FAIL")]
    [InlineData("error", 1, 0, 0, "WARN")]       // some parts measured: not a pass, not a measured failure
    [InlineData("error", 1, 1, 0, "WARN")]       // a failing leaf under an errored root decided nothing: a failure that decides
                                                 // makes the root itself fail (B10k). B10b stored FAIL here (review round 4, B10m)
    [InlineData("error", 0, 0, 0, "PENDING")]    // nothing measured: no verdict
    [InlineData("skipped", 0, 0, 0, "PENDING")]
    public void RunVerdict_IsAVerdictTheSummarySchemaAllows_AndNeverFAILForANonVerdict(
        string label, int passed, int failed, int warnings, string verdict)
    {
        var stats = new RunStats(passed + failed + warnings + 1, passed, failed, warnings, 1);

        Assert.Equal(verdict, Score(label, label == "pass").RunVerdict(stats));
    }

    [Theory]
    [InlineData("PASS", "PASS", "PASS")]
    [InlineData("PASS", "FAIL", "FAIL")]
    [InlineData("ERROR", "FAIL", "FAIL")]
    [InlineData("ERROR", "PASS", "ERROR")]
    [InlineData("WARN", "PASS", "WARN")]
    [InlineData("PASS", "SKIPPED", "WARN")]      // a group that passed on part of its checks is not a clean pass
    [InlineData("SKIPPED", "SKIPPED", "SKIPPED")]
    [InlineData("ERROR", "WARN", "ERROR")]
    public void CombineReportStatus_FailThenErrorThenWarnWin(string a, string b, string combined)
    {
        Assert.Equal(combined, EvalScoreExtensions.CombineReportStatus(a, b));
        Assert.Equal(combined, EvalScoreExtensions.CombineReportStatus(b, a));
    }

    private static EvalResult Leaf(string key, string label, bool passed) => new(
        new(key, key, "safety-security", "1.0.0"), new EvalScore(passed ? 1.0 : 0.0, null, label, passed, null, "none", null),
        new(null, null, null, null, null), new("atomic-llm", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private static EvalResult Root(string label, params EvalResult[] leaves) => new(
        new("agentic.safety", "Safety", "safety-security", "1.1.0"), new EvalScore(1.0, null, label, label == "pass", 0.9, "none", null),
        new(null, null, null, leaves, null), new("composite", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    [Fact]
    public void TheAgenticSummary_ShowsAnErroredCheckAsERROR_AndRecommendsNoReviewOfIt()
    {
        var root = Root("error", Leaf("hate_unfairness", "error", false), Leaf("unsafe_tool_use", "pass", true));

        var summary = new AgentEval.Evals.Agentic.Reporting.AgenticSummaryBuilder().Build(root);
        var recommendations = AgentEval.Evals.Agentic.Reporting.AgenticRecommendationExtractor.Build(root);

        Assert.Equal("ERROR", summary.OverallStatus);
        Assert.Equal("ERROR", summary.PerEvaluator["hate_unfairness"].Status);
        Assert.Equal("PASS", summary.PerEvaluator["unsafe_tool_use"].Status);
        Assert.DoesNotContain(recommendations, r => r.Contains("Review failures in hate_unfairness", StringComparison.Ordinal));
        Assert.DoesNotContain(recommendations, r => r.Contains("Review failures in agentic.safety", StringComparison.Ordinal));
        Assert.Contains(recommendations, r => r.StartsWith("hate_unfairness produced no verdict", StringComparison.Ordinal));
    }

    private static EvalResult Severe(string key, string label, bool passed, string severity, double? value = null) => new(
        new(key, key, "test", "1.0.0"), new EvalScore(value ?? (passed ? 1.0 : 0.1), null, label, passed, null, severity, null),
        new(null, null, null, null, null), new("atomic-llm", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private sealed class Fixed(EvalResult result) : IEval
    {
        public string Key => result.Metric.Key;
        public string Name => Key;
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(result);
    }

    [Fact]
    public async Task AMeasuredCriticalFailure_DecidesTheVerdict_EvenBesideAnErroredArticle()
    {
        // Review round 3 H2: one article's judge errored, another article failed at critical (0.1). Even had the errored
        // article passed, the pillar's average is 0.55 against its 0.85: the threshold decides the fail at the pillar
        // itself (B10k), and the preset reads it. It read ERROR, and its stored summary WARN.
        var pillar = new CompositeEval("pillar", "Pillar", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("art.a", "error", false, "none")), 0.5),
             new EvalComponent(new Fixed(Severe("art.b", "fail", false, "critical")), 0.5)],
            WeightedSumAggregation.Instance, threshold: 0.85);
        var preset = new CompositeEval("preset", "Preset", "test", "1.0.0",
            [new EvalComponent(pillar, 1.0)], WeightedSumAggregation.Instance, threshold: 0.85) { SeverityCapsThreshold = true };
        var input = new EvalInput("q", "r");

        var pillarResult = await pillar.EvaluateAsync(input);
        var result = await preset.EvaluateAsync(input);

        Assert.Equal("fail", pillarResult.Score.Label);
        Assert.Contains("cannot reach the threshold", pillarResult.Details.Summary);
        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("critical", result.Score.Severity);
        Assert.Equal("FAIL", AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary(result, "run").Verdict);
    }

    [Theory]
    [InlineData(null, false, "critical", 0.10, "fail")]   // severity rule: the critical failure decides
    [InlineData(null, false, "medium", 0.10, "error")]    // a medium failure under the severity rule is a warn at most: the error stands
    [InlineData(0.85, false, "critical", 0.80, "error")]  // threshold only: (1.0 + 0.80) / 2 = 0.90, the missing part could pass it
    [InlineData(0.85, false, "critical", 0.10, "fail")]   // threshold only: (1.0 + 0.10) / 2 = 0.55, decided whatever it scored (B10k)
    [InlineData(0.85, true, "critical", 0.80, "fail")]    // threshold + SeverityCapsThreshold: the severity decides
    public async Task ARequiredError_GivesWayOnlyToAFailureThatIsDecided(double? threshold, bool caps, string severity, double failedValue, string label)
    {
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("errored", "error", false, "none")), 0.5),
             new EvalComponent(new Fixed(Severe("failed", "fail", false, severity, failedValue)), 0.5)],
            WeightedSumAggregation.Instance, threshold) { SeverityCapsThreshold = caps };

        Assert.Equal(label, (await composite.EvaluateAsync(new EvalInput("q", "r"))).Score.Label);
    }

    [Fact]
    public async Task TheDecidedNote_CountsTheErroredParts()
    {
        // Review round 5 L-3 (B10x): the note said "even if it had passed" with two required parts errored.
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("e1", "error", false, "none")), 1.0),
             new EvalComponent(new Fixed(Severe("e2", "error", false, "none")), 1.0),
             new EvalComponent(new Fixed(Severe("f", "fail", false, "medium", 0.10)), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.85);   // best case (1 + 1 + 0.1) / 3 = 0.70

        var result = await composite.EvaluateAsync(new EvalInput("q", "r"));

        Assert.Equal("fail", result.Score.Label);
        Assert.Contains("2 required parts produced no verdict", result.Details.Summary);
        Assert.Contains("even if they had passed", result.Details.Summary);
    }

    // The real GDPR / EU tree: threshold-only articles of scenarios, severity-rule pillars, a severity-capped preset.
    private static (CompositeEval Article, CompositeEval Pillar, CompositeEval Preset) GdprShape(EvalResult firstScenario)
    {
        var article = new CompositeEval("art.x", "Art X", "test", "1.0.0",
            [new EvalComponent(new Fixed(firstScenario), 1.0),
             new EvalComponent(new Fixed(Severe("s2", "fail", false, "critical", 0.60)), 1.0),
             new EvalComponent(new Fixed(Severe("s3", "pass", true, "none")), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.70);
        var pillar = new CompositeEval("pillar", "Pillar", "test", "1.0.0",
            [new EvalComponent(article, 1.0), new EvalComponent(new Fixed(Severe("art.y", "pass", true, "none")), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);
        var preset = new CompositeEval("preset", "Preset", "test", "1.0.0",
            [new EvalComponent(pillar, 1.0)], WeightedSumAggregation.Instance, threshold: 0.85) { SeverityCapsThreshold = true };
        return (article, pillar, preset);
    }

    [Fact]
    public async Task AnErroredArticle_DecidesNothingAboveIt_ThoughItsThresholdAbsorbedACriticalScenarioFailure()
    {
        // Review round 4 H1: since B10b the pillar read the errored article's severity (critical, from s2, a scenario
        // failure the article's 0.70 threshold absorbs: with s1 passing it reads pass 0.867) as decided, so one judge
        // glitch turned ERROR (exit 11) into FAIL (exit 9).
        var input = new EvalInput("q", "r");
        var (article, pillar, preset) = GdprShape(Severe("s1", "error", false, "none"));
        var (_, okPillar, okPreset) = GdprShape(Severe("s1", "pass", true, "none"));

        var articleResult = await article.EvaluateAsync(input);
        Assert.Equal("error", articleResult.Score.Label);
        Assert.Equal("none", articleResult.Score.Severity);     // an error has no verdict, so no severity
        Assert.Equal("error", (await pillar.EvaluateAsync(input)).Score.Label);
        Assert.Equal("error", (await preset.EvaluateAsync(input)).Score.Label);
        Assert.Equal("pass", (await okPillar.EvaluateAsync(input)).Score.Label);   // the counterfactual: nothing was decided
        Assert.Equal("pass", (await okPreset.EvaluateAsync(input)).Score.Label);
    }

    [Fact]
    public async Task AnOptionalFailure_InsideAnErroredChild_DecidesNothingAboveIt()
    {
        var child = new CompositeEval("child", "Child", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("judge", "error", false, "none")), 1.0),
             new EvalComponent(new Fixed(Severe("extra", "fail", false, "critical")), 1.0) { Required = false }],
            WeightedSumAggregation.Instance, threshold: null);
        var parent = new CompositeEval("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 1.0)], WeightedSumAggregation.Instance, threshold: null);

        Assert.Equal("error", (await child.EvaluateAsync(new EvalInput("q", "r"))).Score.Label);
        Assert.Equal("error", (await parent.EvaluateAsync(new EvalInput("q", "r"))).Score.Label);
    }

    [Theory]
    [InlineData("agentic")]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public void ARunWhoseRootErrored_IsStoredAsWARN_NotFAIL(string runner)
    {
        var root = Root("error", Leaf("a", "error", false), Leaf("b", "pass", true));

        var summary = runner switch
        {
            "agentic" => AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner.BuildSummary(root, "run"),
            "gdpr" => AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary(root, "run"),
            _ => AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner.BuildSummary(root, "run"),
        };

        Assert.Equal("WARN", summary.Verdict);
    }

    [Fact]
    public void NoReportCountsOrListsAResultWithNoVerdictAsAFailure()
    {
        // Review round 4 M6 (B10p), the class: a report that counts or lists "failures" as !Score.Passed takes in errored,
        // skipped, needs-review and withheld results (the GDPR/EU summaries' ScenariosFailed and two PDFs' "Top criteria
        // failures" did). A measured failure is ReportStatus() == "FAIL".
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var pattern = new System.Text.RegularExpressions.Regex(@"\.(Where|Count)\(\s*\w+\s*=>\s*!\w+\.Score\.Passed\s*\)");
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Reporting{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => pattern.IsMatch(x.Text))
            .Select(x => $"{Path.GetFileName(x.File)}:{x.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "failure predicates on !Score.Passed: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task AnErroredRoot_IsStoredAsWARN_EvenBesideAFailureTheTreeSaysCannotDecide()
    {
        // Review round 4 M3 (B10m): RunVerdict read FAIL off the leaf counts, so the stored summary contradicted the root
        // and exit 11. (A) agentic: a Fail-effect check errored, a quality check (Warn effect) failed. (B) GDPR: a scenario
        // errored beside a medium scenario failure under a severity-rule pillar.
        var input = new EvalInput("q", "r");
        var agentic = new CompositeEval("agentic.quality", "Quality", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("groundedness", "error", false, "none")), 1.0) { OnFailure = ComponentFailureEffect.Fail },
             new EvalComponent(new Fixed(Severe("fluency", "fail", false, "low", 0.3)), 1.0) { OnFailure = ComponentFailureEffect.Warn }],
            WeightedSumAggregation.Instance, threshold: null);
        var article = new CompositeEval("art", "Art", "test", "1.0.0",
            [new EvalComponent(new Fixed(Severe("s1", "error", false, "none")), 1.0),
             new EvalComponent(new Fixed(Severe("s2", "fail", false, "medium", 0.5)), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);

        var agenticResult = await agentic.EvaluateAsync(input);
        var gdprResult = await article.EvaluateAsync(input);

        Assert.Equal("error", agenticResult.Score.Label);
        Assert.Equal("error", gdprResult.Score.Label);
        Assert.Equal("WARN", AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner.BuildSummary(agenticResult, "run").Verdict);
        Assert.Equal("WARN", AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary(gdprResult, "run").Verdict);
        Assert.Equal("WARN", AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner.BuildSummary(gdprResult, "run").Verdict);
    }
}
