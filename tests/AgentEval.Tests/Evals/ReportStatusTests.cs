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
}
