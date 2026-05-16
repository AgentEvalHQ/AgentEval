// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.EuAiActBenchmark.Reporting;

/// <summary>
/// Unit tests for <see cref="CrossRegulationLinker"/>.
/// </summary>
public class CrossRegulationLinkerTests
{
    // ── Stub builders ─────────────────────────────────────────────────────────

    /// <summary>Builds a minimal <see cref="GdprComplianceEvidence"/> with the supplied PerArticle map.</summary>
    private static GdprComplianceEvidence BuildGdprEvidence(
        IReadOnlyDictionary<string, GdprArticleSummary> perArticle)
    {
        var summary = new GdprSummary(
            OverallScore: 1.0,
            OverallStatus: "PASS",
            PerPillar: new Dictionary<string, GdprPillarSummary>(),
            PerArticle: perArticle);

        var subject  = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var sourceRun = new SourceRunRef("run-gdpr", "sha256:" + new string('0', 64));

        var base_ = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: sourceRun,
            Controls: Array.Empty<EvidenceControl>(),
            Summary: new EvidenceSummary(0, 0, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "test", "stub"));

        var compositeTree = new EvalResult(
            Metric: new("root", "root", "compliance.test", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("stub-llm", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new GdprComplianceEvidence(
            Base: base_,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: Array.Empty<EvalResult>(),
            Recommendations: Array.Empty<AgentEval.GdprBenchmark.Reporting.Recommendation>(),
            Disclaimer: "stub",
            GdprAttestation: new GdprAttestation("mode-a", new Dictionary<string, string>()));
    }

    /// <summary>Builds a minimal <see cref="EuAiActComplianceEvidence"/> with the supplied PerArticle map.</summary>
    private static EuAiActComplianceEvidence BuildEuAiActEvidence(
        IReadOnlyDictionary<string, EuAiActArticleSummary> perArticle)
    {
        var summary = new EuAiActSummary(
            OverallScore: 1.0,
            OverallStatus: "PASS",
            PerPillar: new Dictionary<string, EuAiActPillarSummary>(),
            PerArticle: perArticle);

        var subject  = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var sourceRun = new SourceRunRef("run-eu", "sha256:" + new string('0', 64));

        var base_ = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "EU-AI-Act",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: sourceRun,
            Controls: Array.Empty<EvidenceControl>(),
            Summary: new EvidenceSummary(0, 0, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "test", "stub"));

        var compositeTree = new EvalResult(
            Metric: new("root", "root", "compliance.test", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("stub-llm", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new EuAiActComplianceEvidence(
            Base: base_,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: Array.Empty<EvalResult>(),
            Recommendations: Array.Empty<AgentEval.EuAiActBenchmark.Reporting.Recommendation>(),
            Disclaimer: "stub",
            EuAiActAttestation: new EuAiActAttestation("mode-a", new Dictionary<string, string>()));
    }

    private static GdprArticleSummary GdprArticle(string status) =>
        new(Score: status == "PASS" ? 1.0 : 0.0, Status: status,
            ScenarioCount: 1, ScenariosFailed: status == "PASS" ? 0 : 1, Severity: "high");

    private static EuAiActArticleSummary EuAiArticle(string status) =>
        new(Score: status == "PASS" ? 1.0 : 0.0, Status: status,
            ScenarioCount: 1, ScenariosFailed: status == "PASS" ? 0 : 1, Severity: "high");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_BothPass_ReturnsEmpty()
    {
        var gdprArticles = new Dictionary<string, GdprArticleSummary>
        {
            ["gdpr.art22.automated"]           = GdprArticle("PASS"),
            ["gdpr.art9.special_categories"]   = GdprArticle("PASS"),
            ["gdpr.art13.information_collection"] = GdprArticle("PASS"),
            ["gdpr.art14.information_indirect"]   = GdprArticle("PASS"),
        };
        var euArticles = new Dictionary<string, EuAiActArticleSummary>
        {
            ["eu_ai.art14.human_oversight"]          = EuAiArticle("PASS"),
            ["eu_ai.art5.biometric_cat+realtime_id"] = EuAiArticle("PASS"),
            ["eu_ai.art50.ai_disclosure"]            = EuAiArticle("PASS"),
        };

        var linker = new CrossRegulationLinker();
        var findings = linker.Link(BuildGdprEvidence(gdprArticles), BuildEuAiActEvidence(euArticles));

        Assert.Empty(findings);
    }

    [Fact]
    public void Link_GdprArt22FailEuAct14Fail_LinksFinding()
    {
        var gdprArticles = new Dictionary<string, GdprArticleSummary>
        {
            ["gdpr.art22.automated"] = GdprArticle("FAIL"),
        };
        var euArticles = new Dictionary<string, EuAiActArticleSummary>
        {
            ["eu_ai.art14.human_oversight"] = EuAiArticle("FAIL"),
        };

        var linker = new CrossRegulationLinker();
        var findings = linker.Link(BuildGdprEvidence(gdprArticles), BuildEuAiActEvidence(euArticles));

        var finding = Assert.Single(findings);
        Assert.Equal("gdpr.art22.automated",     finding.GdprControlId);
        Assert.Equal("eu_ai.art14.human_oversight", finding.EuAiActControlId);
        Assert.Equal("Human oversight for legally significant automated decisions", finding.OverlapNature);
        Assert.Equal("FAIL", finding.GdprStatus);
        Assert.Equal("FAIL", finding.EuAiActStatus);
    }

    [Fact]
    public void Link_OnlyOneSideFails_ReturnsEmpty()
    {
        // GDPR art22 fails but EU AI Act art14 passes — no overlap.
        var gdprArticles = new Dictionary<string, GdprArticleSummary>
        {
            ["gdpr.art22.automated"] = GdprArticle("FAIL"),
        };
        var euArticles = new Dictionary<string, EuAiActArticleSummary>
        {
            ["eu_ai.art14.human_oversight"] = EuAiArticle("PASS"),
        };

        var linker = new CrossRegulationLinker();
        var findings = linker.Link(BuildGdprEvidence(gdprArticles), BuildEuAiActEvidence(euArticles));

        Assert.Empty(findings);
    }

    [Fact]
    public void Link_OrdersByJointSeverity()
    {
        // art9/art5 pair: WARN+WARN; art22/art14 pair: FAIL+FAIL — FAIL+FAIL should come first.
        var gdprArticles = new Dictionary<string, GdprArticleSummary>
        {
            ["gdpr.art22.automated"]         = GdprArticle("FAIL"),
            ["gdpr.art9.special_categories"] = GdprArticle("WARN"),
        };
        var euArticles = new Dictionary<string, EuAiActArticleSummary>
        {
            ["eu_ai.art14.human_oversight"]          = EuAiArticle("FAIL"),
            ["eu_ai.art5.biometric_cat+realtime_id"] = EuAiArticle("WARN"),
        };

        var linker = new CrossRegulationLinker();
        var findings = linker.Link(BuildGdprEvidence(gdprArticles), BuildEuAiActEvidence(euArticles));

        Assert.Equal(2, findings.Count);
        Assert.Equal("FAIL", findings[0].GdprStatus);
        Assert.Equal("FAIL", findings[0].EuAiActStatus);
        Assert.Equal("WARN", findings[1].GdprStatus);
        Assert.Equal("WARN", findings[1].EuAiActStatus);
    }

    [Fact]
    public void Link_MissingControlIds_SkipsGracefully()
    {
        // Both evidence wrappers have empty PerArticle dictionaries.
        var gdprArticles   = new Dictionary<string, GdprArticleSummary>();
        var euArticles     = new Dictionary<string, EuAiActArticleSummary>();

        var linker = new CrossRegulationLinker();
        var findings = linker.Link(BuildGdprEvidence(gdprArticles), BuildEuAiActEvidence(euArticles));

        Assert.Empty(findings);
    }
}
