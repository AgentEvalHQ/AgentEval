// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Reporting;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct.Reporting;

/// <summary>
/// Unit tests for <see cref="RecommendationExtractor"/>, mirroring the GDPR extractor's
/// coverage in <c>AgentEval.Tests.Compliance.Gdpr.RecommendationStructuredShapeTests</c>.
/// </summary>
public class RecommendationExtractorTests
{
    private static EvalResult MakeArticleNode(string key, double score, bool passed, string severity) =>
        new(
            Metric: new(key, key, "compliance.test", "1.0"),
            Score: new(score, null, passed ? "pass" : "fail", passed, 0.85, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult MakeErrorArticleNode(string key) =>
        new(
            Metric: new(key, key, "compliance.test", "1.0"),
            Score: new(0.0, null, "error", false, 0.85, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult MakeRoot(params EvalResult[] children) =>
        new(
            Metric: new("root", "root", "compliance.test", "1.0"),
            Score: new(0.5, null, "fail", false, 0.9, "high", null),
            Details: new(null, null, null, children, "CapByWorst"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Build_FailedArticle_ReturnsRecommendation()
    {
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(MakeArticleNode("eu_ai.art9.risk_management", 0.30, false, "high"));

        var recs = extractor.Build(root);

        Assert.Single(recs);
        Assert.Equal("eu_ai.art9.risk_management", recs[0].ControlId);
        Assert.Equal("high", recs[0].Severity);
        Assert.False(string.IsNullOrWhiteSpace(recs[0].Text));
    }

    [Fact]
    public void Build_LeafErrorNode_ExcludedFromRecommendations()
    {
        // A leaf scenario node labelled "error" never reaches WalkArticles' StartsWith("eu_ai.")
        // check directly (scenario ids look like "eu-ai-art15-001", not "eu_ai.art15.robustness"),
        // so this only matters if an "error"-labelled node is ever placed at the control level
        // itself. Keeping this case documents that the extractor still ignores it directly too.
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(
            MakeErrorArticleNode("eu_ai.art15.robustness"),
            MakeArticleNode("eu_ai.art9.risk_management", 0.10, false, "critical"));

        var recs = extractor.Build(root);

        Assert.Single(recs);
        Assert.Equal("eu_ai.art9.risk_management", recs[0].ControlId);
        Assert.DoesNotContain(recs, r => r.ControlId == "eu_ai.art15.robustness");
    }

    [Fact]
    public void Build_ControlSeverityRolledUpToNone_ExcludedFromRecommendations()
    {
        // Regression (the actual bug): a control (article) node's OWN Label is "pass"/"fail" —
        // composites never carry "error", only their leaves do. When EVERY scenario under a
        // control fails as an evaluation-infrastructure "error" (e.g. the agent never produced a
        // real answer — see AgentScenarioEval), WeightedSumAggregation rolls up severity via
        // SeverityRollup.Max over leaf severities, which stays "none". The schema's severity enum
        // rejects "none" entirely, so this control crashed the whole report. Checking the
        // control's own Severity (not Label) is what actually catches this.
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(
            MakeArticleNode("eu_ai.art15.robustness", 0.0, false, "none"),
            MakeArticleNode("eu_ai.art9.risk_management", 0.10, false, "critical"));

        var recs = extractor.Build(root);

        Assert.Single(recs);
        Assert.Equal("eu_ai.art9.risk_management", recs[0].ControlId);
        Assert.DoesNotContain(recs, r => r.ControlId == "eu_ai.art15.robustness");
    }
}
