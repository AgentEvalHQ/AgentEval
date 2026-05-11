// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Articles.Models;
using AgentEval.GdprBenchmark.Articles.Validation;
using AgentEval.GdprBenchmark.Reporting;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// Targeted edge-case tests for components that previously had only happy-path coverage:
/// <see cref="ArticleYamlValidator"/> error branches, <see cref="ArticleScenarioYamlLoader.LoadAllFromAssemblyAsync"/>,
/// and <see cref="MarkdownRenderer"/>'s empty-pillar suppression.
/// </summary>
public class ValidatorAndLoaderEdgeCaseTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScenarioSpec ValidScenario(double weight = 1.0, string id = "s-1") =>
        new(
            Id: id,
            Pattern: "direct",
            Weight: weight,
            Input: "test input",
            EvaluationCriteria: ["criterion A"],
            Granularity: "atomic",
            Sensitive: false,
            ExpectedBehavior: null,
            Tags: []);

    private static ArticleMetadata ValidMetadata(
        string pillar = "Pillar1-Foundations",
        string severity = "low",
        string aggregation = "weighted_sum",
        double passThreshold = 0.70,
        double warnThreshold = 0.50,
        double pillarWeight = 0.10) =>
        new(
            Article: "Article-X",
            Pillar: pillar,
            ControlId: "gdpr.x",
            Title: "X",
            Severity: severity,
            PassThreshold: passThreshold,
            WarnThreshold: warnThreshold,
            PillarWeight: pillarWeight,
            Aggregation: aggregation);

    private static ArticleSpec ValidSpec() =>
        new(ValidMetadata(), [ValidScenario()]);

    // ── ArticleYamlValidator — happy path sanity ──────────────────────────────

    [Fact]
    public void Validator_ValidSpec_IsValid()
    {
        var sut = new ArticleYamlValidator();
        var result = sut.Validate(ValidSpec());
        Assert.True(result.IsValid, result.ErrorsJoined);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_NullSpec_Throws()
    {
        var sut = new ArticleYamlValidator();
        Assert.Throws<ArgumentNullException>(() => sut.Validate(null!));
    }

    // ── ArticleYamlValidator — metadata error branches ────────────────────────

    [Fact]
    public void Validator_MissingControlId_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata() with { ControlId = "" }, [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("control_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidPillar_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(pillar: "NotAPillar"), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("pillar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidSeverity_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(severity: "ultra"), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("severity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidAggregation_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(aggregation: "geometric_mean"), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("aggregation", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Validator_PassThresholdOutOfRange_Fails(double threshold)
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(passThreshold: threshold), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("pass_threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(2.0)]
    public void Validator_WarnThresholdOutOfRange_Fails(double threshold)
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(warnThreshold: threshold), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("warn_threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_PillarWeightOutOfRange_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(pillarWeight: 1.5), [ValidScenario()]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("pillar_weight", StringComparison.OrdinalIgnoreCase));
    }

    // ── ArticleYamlValidator — scenario error branches ────────────────────────

    [Fact]
    public void Validator_NoScenarios_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(ValidMetadata(), []);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least one scenario", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_WeightsDoNotSumToOne_Fails()
    {
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(
            ValidMetadata(),
            [ValidScenario(weight: 0.4, id: "a"), ValidScenario(weight: 0.4, id: "b")]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sum to 1.0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_WeightsSumToOne_WithinTolerance_IsValid()
    {
        // 0.333 * 3 = 0.999 — within the 0.001 tolerance.
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(
            ValidMetadata(),
            [ValidScenario(weight: 0.333, id: "a"),
             ValidScenario(weight: 0.333, id: "b"),
             ValidScenario(weight: 0.334, id: "c")]);

        var result = sut.Validate(spec);

        Assert.True(result.IsValid, result.ErrorsJoined);
    }

    [Fact]
    public void Validator_ScenarioMissingId_Fails()
    {
        var sut = new ArticleYamlValidator();
        var bad = ValidScenario() with { Id = "" };
        var spec = new ArticleSpec(ValidMetadata(), [bad]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("scenario.id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidScenarioPattern_Fails()
    {
        var sut = new ArticleYamlValidator();
        var bad = ValidScenario() with { Pattern = "exotic" };
        var spec = new ArticleSpec(ValidMetadata(), [bad]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidGranularity_Fails()
    {
        var sut = new ArticleYamlValidator();
        var bad = ValidScenario() with { Granularity = "molecular" };
        var spec = new ArticleSpec(ValidMetadata(), [bad]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("granularity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ScenarioWeightOutOfRange_Fails()
    {
        var sut = new ArticleYamlValidator();
        var bad = ValidScenario() with { Weight = 2.5 };
        var spec = new ArticleSpec(ValidMetadata(), [bad]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        // Hits both "weight in [0,1]" and "weights must sum to 1.0" — assert at least the per-scenario one.
        Assert.Contains(result.Errors, e => e.Contains("weight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_EmptyEvaluationCriteria_Fails()
    {
        var sut = new ArticleYamlValidator();
        var bad = ValidScenario() with { EvaluationCriteria = [] };
        var spec = new ArticleSpec(ValidMetadata(), [bad]);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("evaluation_criteria", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_AggregatesMultipleErrors()
    {
        // Confirms the validator collects all errors instead of short-circuiting on the first.
        var sut = new ArticleYamlValidator();
        var spec = new ArticleSpec(
            ValidMetadata(pillar: "NotAPillar", severity: "ultra", aggregation: "exotic"),
            []);

        var result = sut.Validate(spec);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 4,
            $"expected at least 4 errors (pillar, severity, aggregation, no scenarios); got {result.Errors.Count}: {result.ErrorsJoined}");
    }

    // ── LoadAllFromAssemblyAsync ──────────────────────────────────────────────

    [Fact]
    public async Task LoadAllFromAssemblyAsync_NullAssembly_Throws()
    {
        var sut = new ArticleScenarioYamlLoader();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.LoadAllFromAssemblyAsync(null!));
    }

    [Fact]
    public async Task LoadAllFromAssemblyAsync_GdprAssembly_Loads21RealArticles()
    {
        // The GDPR sample assembly carries the 21 article YAMLs as embedded resources.
        var sut = new ArticleScenarioYamlLoader();
        var asm = typeof(AgentEval.GdprBenchmark.Articles.Loading.ArticleScenarioYamlLoader).Assembly;

        var specs = await sut.LoadAllFromAssemblyAsync(asm, includeTestFixtures: false);

        // 21 GDPR controls per the catalog (excluding the test-fixture YAML).
        Assert.Equal(21, specs.Count);
        Assert.Contains(specs, s => s.Metadata.ControlId == "gdpr.art17.erasure");
        Assert.Contains(specs, s => s.Metadata.ControlId == "gdpr.art9.special_categories");
    }

    [Fact]
    public async Task LoadAllFromAssemblyAsync_IncludeTestFixturesFlag_Returns22()
    {
        var sut = new ArticleScenarioYamlLoader();
        var asm = typeof(AgentEval.GdprBenchmark.Articles.Loading.ArticleScenarioYamlLoader).Assembly;

        var specs = await sut.LoadAllFromAssemblyAsync(asm, includeTestFixtures: true);

        Assert.Equal(22, specs.Count);
        Assert.Contains(specs, s => s.Metadata.ControlId == "gdpr.test.fixture");
    }

    [Fact]
    public async Task LoadAllFromAssemblyAsync_AssemblyWithoutYamls_ReturnsEmpty()
    {
        // typeof(int).Assembly = System.Private.CoreLib — has no GDPR YAML resources.
        var sut = new ArticleScenarioYamlLoader();
        var specs = await sut.LoadAllFromAssemblyAsync(typeof(int).Assembly);

        Assert.Empty(specs);
    }

    // ── MarkdownRenderer empty-pillar suppression ─────────────────────────────

    [Fact]
    public void MarkdownRenderer_EmptyPerPillar_OmitsPillarSectionEntirely()
    {
        // Smoke preset produces an empty PerPillar dict; the renderer should NOT emit
        // the "## Per-pillar" header or the empty table.
        var renderer = new MarkdownRenderer();
        var evidence = MakeEvidenceWithEmptyPillarMap();

        var md = renderer.Render(evidence);

        Assert.DoesNotContain("## Per-pillar", md);
        Assert.DoesNotContain("| Pillar | Score |", md);
        // Per-article section is still rendered
        Assert.Contains("## Per-article", md);
    }

    [Fact]
    public void MarkdownRenderer_NonEmptyPerPillar_EmitsPillarSection()
    {
        var renderer = new MarkdownRenderer();
        var evidence = MakeEvidenceWithEmptyPillarMap() with
        {
            Summary = MakeEvidenceWithEmptyPillarMap().Summary with
            {
                PerPillar = new Dictionary<string, GdprPillarSummary>
                {
                    ["Pillar1-Foundations"] = new(0.9, "PASS", [])
                }
            }
        };

        var md = renderer.Render(evidence);

        Assert.Contains("## Per-pillar", md);
        Assert.Contains("Pillar1-Foundations", md);
    }

    private static GdprComplianceEvidence MakeEvidenceWithEmptyPillarMap()
    {
        var subject = new AgentEval.Output.SubjectIdentity(AgentEval.Output.SubjectKind.Agent, "T");
        var sourceRun = new AgentEval.Output.SourceRunRef("run", "sha256:" + new string('0', 64));
        var baseEvidence = new AgentEval.Output.ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: sourceRun,
            Controls: [],
            Summary: new AgentEval.Output.EvidenceSummary(0, 0, 0, 0, "PASS"),
            Attestation: new AgentEval.Output.Attestation("0.0.0", null, "test", "stub"));

        var atomic = new AgentEval.Evals.EvalResult(
            Metric: new("k", "n", "c", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: atomic,
            Summary: new GdprSummary(
                OverallScore: 1.0,
                OverallStatus: "PASS",
                PerPillar: new Dictionary<string, GdprPillarSummary>(),
                PerArticle: new Dictionary<string, GdprArticleSummary>
                {
                    ["gdpr.art17.erasure"] = new(1.0, "PASS", 1, 0, "high")
                }),
            CriticalFindings: [],
            Recommendations: [],
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation("mode-a", new Dictionary<string, string>()));
    }
}
