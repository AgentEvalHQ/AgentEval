// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Unit tests for Phase-4 G4.5-G4.8 reporting components:
/// <see cref="SummaryBuilder"/>, <see cref="CriticalFindingExtractor"/>,
/// <see cref="RecommendationExtractor"/>, and <see cref="MarkdownRenderer"/>.
/// </summary>
public class ReportingUnitTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EvalResult MakeLeaf(string key, double score, bool passed, string severity = "none") =>
        new(
            Metric: new(key, key, "compliance.test", "1.0"),
            Score: new(score, null, passed ? "pass" : "fail", passed, 0.75, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult MakeComposite(string key, double score, bool passed, string severity,
        IReadOnlyList<EvalResult> children) =>
        new(
            Metric: new(key, key, "compliance.test", "1.0"),
            Score: new(score, null, passed ? "pass" : "fail", passed, 0.85, severity, null),
            Details: new(null, null, null, children, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static ArticlesRegistry BuildRegistry()
    {
        var loader = new ArticleScenarioYamlLoader();
        var judge = new StubEvaluator(100);
        var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, articleBuilder);
    }

    private sealed class StubEvaluator : AgentEval.Core.IEvaluator
    {
        private readonly int _score;
        public StubEvaluator(int score) { _score = score; }

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = _score,
                Summary = "stub",
                CriteriaResults = criteria
                    .Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
    }

    // ── CriticalFindingExtractor ─────────────────────────────────────────────

    [Fact]
    public void CriticalFindingExtractor_NullRoot_Throws()
    {
        var extractor = new CriticalFindingExtractor();
        Assert.Throws<ArgumentNullException>(() => extractor.Find(null!));
    }

    [Fact]
    public void CriticalFindingExtractor_NoLeaves_ReturnsEmpty()
    {
        var extractor = new CriticalFindingExtractor();
        var leaf = MakeLeaf("scenario-a", 0.90, true, "none");
        var result = extractor.Find(leaf);
        Assert.Empty(result);
    }

    [Fact]
    public void CriticalFindingExtractor_CriticalFailingLeaf_IsReturned()
    {
        var extractor = new CriticalFindingExtractor();
        var critLeaf = MakeLeaf("scenario-critical", 0.10, false, "critical");
        var result = extractor.Find(critLeaf);
        Assert.Single(result);
        Assert.Equal("scenario-critical", result[0].Metric.Key);
    }

    [Fact]
    public void CriticalFindingExtractor_CriticalPassingLeaf_IsExcluded()
    {
        var extractor = new CriticalFindingExtractor();
        // Critical severity but passed — should not appear in critical findings
        var critPassing = MakeLeaf("scenario-crit-pass", 0.95, true, "critical");
        var result = extractor.Find(critPassing);
        Assert.Empty(result);
    }

    [Fact]
    public void CriticalFindingExtractor_OrderedByScoreAscending()
    {
        var extractor = new CriticalFindingExtractor();
        var bad = MakeLeaf("bad", 0.05, false, "critical");
        var worse = MakeLeaf("worse", 0.02, false, "critical");
        var ok = MakeLeaf("ok", 0.09, false, "critical");
        var composite = MakeComposite("root", 0.05, false, "critical", [bad, worse, ok]);

        var findings = extractor.Find(composite);
        Assert.Equal(3, findings.Count);
        Assert.Equal("worse", findings[0].Metric.Key); // lowest score first
        Assert.Equal("bad", findings[1].Metric.Key);
        Assert.Equal("ok", findings[2].Metric.Key);
    }

    // ── RecommendationExtractor ───────────────────────────────────────────────

    [Fact]
    public void RecommendationExtractor_NullRoot_Throws()
    {
        var extractor = new RecommendationExtractor();
        Assert.Throws<ArgumentNullException>(() => extractor.Build(null!));
    }

    [Fact]
    public void RecommendationExtractor_AllPassing_ReturnsEmpty()
    {
        var extractor = new RecommendationExtractor();
        // A passing article node — should produce no recommendation
        var articleResult = MakeComposite("gdpr.art17.erasure", 0.90, true, "none",
            [MakeLeaf("s1", 0.90, true, "none")]);
        var root = MakeComposite("root", 0.90, true, "none", [articleResult]);
        var recs = extractor.Build(root);
        Assert.Empty(recs);
    }

    [Fact]
    public void RecommendationExtractor_FailingArticle_ReturnsRecommendation()
    {
        var extractor = new RecommendationExtractor();
        var articleResult = MakeComposite("gdpr.art17.erasure", 0.30, false, "high",
            [MakeLeaf("s1", 0.30, false, "high")]);
        var root = MakeComposite("root", 0.30, false, "high", [articleResult]);
        var recs = extractor.Build(root);
        Assert.Single(recs);
        Assert.Equal("gdpr.art17.erasure", recs[0].ControlId);
        Assert.Equal("high", recs[0].Severity);
        Assert.Contains("deletion", recs[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecommendationExtractor_UnknownControlId_ReturnsFallback()
    {
        var extractor = new RecommendationExtractor();
        var articleResult = MakeComposite("gdpr.art99.unknown", 0.10, false, "high",
            [MakeLeaf("s1", 0.10, false, "high")]);
        var root = MakeComposite("root", 0.10, false, "high", [articleResult]);
        var recs = extractor.Build(root);
        Assert.Single(recs);
        Assert.Equal("gdpr.art99.unknown", recs[0].ControlId);
        Assert.Contains("gdpr.art99.unknown", recs[0].Text);
    }

    [Fact]
    public void RecommendationExtractor_Deduplicates_SameArticle()
    {
        var extractor = new RecommendationExtractor();
        // Two nodes referencing the same article key should produce one recommendation
        var article1 = MakeComposite("gdpr.art17.erasure", 0.30, false, "high",
            [MakeLeaf("s1", 0.30, false, "high")]);
        var article2 = MakeComposite("gdpr.art17.erasure", 0.20, false, "high",
            [MakeLeaf("s2", 0.20, false, "high")]);
        var root = MakeComposite("root", 0.25, false, "high", [article1, article2]);
        var recs = extractor.Build(root);
        Assert.Single(recs);
    }

    // ── SummaryBuilder ────────────────────────────────────────────────────────

    [Fact]
    public void SummaryBuilder_NullArticles_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SummaryBuilder(null!));
    }

    [Fact]
    public void SummaryBuilder_NullRoot_Throws()
    {
        var registry = BuildRegistry();
        var builder = new SummaryBuilder(registry);
        Assert.Throws<ArgumentNullException>(() => builder.Build(null!));
    }

    [Fact]
    public void SummaryBuilder_EmptySubResults_ReturnsEmptySummary()
    {
        var registry = BuildRegistry();
        var builder = new SummaryBuilder(registry);
        var root = MakeLeaf("root", 0.90, true, "none");
        var summary = builder.Build(root);
        Assert.Empty(summary.PerPillar);
        Assert.Empty(summary.PerArticle);
        Assert.Equal("PASS", summary.OverallStatus);
    }

    [Fact]
    public void SummaryBuilder_MapLabelToStatus_FailForUnknown()
    {
        var registry = BuildRegistry();
        var builder = new SummaryBuilder(registry);
        // Create a root with a single "article" that has an unrecognised label
        var leaf = MakeLeaf("gdpr.art17.erasure", 0.50, false, "high");
        // Override the label by constructing with a different label
        var badLabelLeaf = new EvalResult(
            Metric: new("gdpr.art17.erasure", "erasure", "test", "1.0"),
            Score: new(0.50, null, "weird-label", false, 0.85, "high", null),
            Details: new(null, null, null, [MakeLeaf("s1", 0.50, false, "high")], null),
            Provenance: new("atomic", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
        var root = MakeComposite("root", 0.50, false, "high", [badLabelLeaf]);
        var summary = builder.Build(root);
        Assert.Equal("FAIL", summary.PerArticle["gdpr.art17.erasure"].Status);
    }

    // ── MarkdownRenderer ──────────────────────────────────────────────────────

    [Fact]
    public void MarkdownRenderer_NullEvidence_Throws()
    {
        var renderer = new MarkdownRenderer();
        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
    }

    [Fact]
    public void MarkdownRenderer_ContainsRequiredSections()
    {
        var renderer = new MarkdownRenderer();
        var evidence = MakeSampleEvidence();
        var md = renderer.Render(evidence);

        Assert.Contains("# GDPR Compliance", md);
        Assert.Contains(GDPRComplianceReporter.Disclaimer, md);
        Assert.Contains("Per-article", md);
        Assert.Contains("**Subject**", md);
        Assert.Contains("**Overall**", md);
    }

    [Fact]
    public void MarkdownRenderer_NoCriticalFindings_SectionAbsent()
    {
        var renderer = new MarkdownRenderer();
        var evidence = MakeSampleEvidence(criticalFindings: []);
        var md = renderer.Render(evidence);
        Assert.DoesNotContain("## Critical findings", md);
    }

    [Fact]
    public void MarkdownRenderer_WithCriticalFindings_SectionPresent()
    {
        var renderer = new MarkdownRenderer();
        var leaf = MakeLeaf("gdpr.art9.special_categories", 0.10, false, "critical");
        var evidence = MakeSampleEvidence(criticalFindings: [leaf]);
        var md = renderer.Render(evidence);
        Assert.Contains("## Critical findings", md);
        Assert.Contains("gdpr.art9.special_categories", md);
    }

    [Fact]
    public void MarkdownRenderer_PresetTitleCapitalized()
    {
        var renderer = new MarkdownRenderer();
        var evidence = MakeSampleEvidence(preset: "smoke");
        var md = renderer.Render(evidence);
        Assert.Contains("Smoke Preset", md);
    }

    // ── GDPRComplianceReporter constant ──────────────────────────────────────

    [Fact]
    public void GDPRComplianceReporter_Disclaimer_ContainsKeyPhrases()
    {
        Assert.Contains("first-line screening tool", GDPRComplianceReporter.Disclaimer);
        Assert.Contains("DPO sign-off", GDPRComplianceReporter.Disclaimer);
        Assert.Contains("not a legal compliance attestation", GDPRComplianceReporter.Disclaimer);
    }

    // ── GdprBenchmark factories ────────────────────────────────────────────

    [Fact]
    public void GdprBenchmark_Smoke_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GdprBenchmark.Smoke(null!));
    }

    [Fact]
    public void GdprBenchmark_Standard_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GdprBenchmark.Standard(null!));
    }

    [Fact]
    public void GdprBenchmark_Smoke_Key_IsCorrect()
    {
        var registry = BuildRegistry();
        var smoke = GdprBenchmark.Smoke(registry);
        Assert.Equal("gdpr.compliance.smoke", smoke.Key);
        Assert.Equal(5, smoke.Components.Count);
        Assert.Equal(0.80, smoke.Threshold);
    }

    [Fact]
    public void GdprBenchmark_Standard_Key_IsCorrect()
    {
        var registry = BuildRegistry();
        var standard = GdprBenchmark.Standard(registry);
        Assert.Equal("gdpr.compliance.standard", standard.Key);
        // plan-13 T1.1 introduced Pillar 6 Governance (5 → 6 pillars)
        Assert.Equal(6, standard.Components.Count);
        Assert.Equal(0.85, standard.Threshold);
    }

    [Fact]
    public void GdprBenchmark_Standard_WeightsSumToOne()
    {
        var registry = BuildRegistry();
        var standard = GdprBenchmark.Standard(registry);
        var weightSum = standard.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, weightSum, precision: 10);
    }

    [Fact]
    public void GdprBenchmark_Smoke_WeightsSumToOne()
    {
        var registry = BuildRegistry();
        var smoke = GdprBenchmark.Smoke(registry);
        var weightSum = smoke.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, weightSum, precision: 10);
    }

    // ── GdprBenchmark.AuditGrade ─────────────────────────────────────────────

    [Fact]
    public void GdprBenchmark_AuditGrade_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GdprBenchmark.AuditGrade(null!));
    }

    [Fact]
    public void GdprBenchmark_AuditGrade_Key_IsCorrect()
    {
        var registry = BuildRegistry();
        var auditGrade = GdprBenchmark.AuditGrade(registry);
        Assert.Equal("gdpr.compliance.auditgrade", auditGrade.Key);
        // plan-13 T1.1 introduced Pillar 6 Governance (5 → 6 pillars)
        Assert.Equal(6, auditGrade.Components.Count);
        Assert.Equal(0.90, auditGrade.Threshold);
        Assert.Equal("CapByWorst", auditGrade.Aggregation.Name);
    }

    [Fact]
    public void GdprBenchmark_AuditGrade_WeightsSumToOne()
    {
        var registry = BuildRegistry();
        var auditGrade = GdprBenchmark.AuditGrade(registry);
        var weightSum = auditGrade.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, weightSum, precision: 10);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GdprComplianceEvidence MakeSampleEvidence(
        string preset = "standard",
        IReadOnlyList<EvalResult>? criticalFindings = null,
        IReadOnlyList<AgentEval.Compliance.Gdpr.Reporting.Recommendation>? recommendations = null)
    {
        criticalFindings ??= [];
        recommendations ??= [];

        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("run-001", "hash-abc"),
            Controls: [],
            Summary: new EvidenceSummary(5, 4, 0, 1, "WARN"),
            Attestation: new Attestation("0.0.0", null, "test", "stub"));

        var summary = new GdprSummary(
            OverallScore: 0.85,
            OverallStatus: "PASS",
            PerPillar: new Dictionary<string, GdprPillarSummary>
            {
                ["Pillar1-Foundations"] = new(0.90, "PASS", [])
            },
            PerArticle: new Dictionary<string, GdprArticleSummary>
            {
                ["gdpr.art17.erasure"] = new(0.90, "PASS", 3, 0, "high")
            });

        var compositeTree = MakeComposite("root", 0.85, true, "none",
            [MakeLeaf("gdpr.art17.erasure", 0.90, true, "none")]);

        return new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: preset,
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: criticalFindings,
            Recommendations: recommendations,
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation("mode-a",
                new Dictionary<string, string> { ["gdpr-judge-system"] = "v1" }));
    }
}
