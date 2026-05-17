// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.GdprBenchmark.Reporting.Pdf;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.GdprBenchmark;

/// <summary>
/// Unit and integration tests for <see cref="GDPRPdfRenderer"/> (Phase 6, G6.1-G6.8).
/// </summary>
public class GDPRPdfRendererTests : IDisposable
{
    private readonly string _tempDir;

    public GDPRPdfRendererTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agenteval-pdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── AAA helpers ────────────────────────────────────────────────────────

    private static GdprComplianceEvidence MakeMinimalEvidence(
        string preset = "standard",
        bool hasPillars = false)
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "PdfTestAgent");
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("run-pdf-001", "sha256:" + new string('a', 64)),
            Controls: [],
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval.GdprBenchmark", "stub"));

        var perPillar = hasPillars
            ? (IReadOnlyDictionary<string, GdprPillarSummary>)new Dictionary<string, GdprPillarSummary>
            {
                ["Pillar1-Foundations"] = new(0.90, "PASS", [])
            }
            : new Dictionary<string, GdprPillarSummary>();

        var summary = new GdprSummary(
            OverallScore: 0.90,
            OverallStatus: "PASS",
            PerPillar: perPillar,
            PerArticle: new Dictionary<string, GdprArticleSummary>
            {
                ["gdpr.art17.erasure"] = new(0.90, "PASS", 2, 0, "high")
            });

        var leaf = new EvalResult(
            Metric: new("gdpr.art17.erasure-001", "Art.17 scenario", "compliance.gdpr", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var articleComposite = new EvalResult(
            Metric: new("gdpr.art17.erasure", "Right to Erasure", "compliance.gdpr", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, [leaf], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var compositeTree = new EvalResult(
            Metric: new("gdpr.compliance.standard", "GDPR Compliance", "compliance.gdpr", "1.0"),
            Score: new(0.90, null, "pass", true, 0.85, "none", null),
            Details: new(null, null, null, [articleComposite], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: preset,
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: [],
            Recommendations: Array.Empty<AgentEval.GdprBenchmark.Reporting.Recommendation>(),
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation(
                "mode-a",
                new Dictionary<string, string> { ["gdpr-judge-system"] = "v1", ["per-criterion"] = "v1" }));
    }

    private static ArticlesRegistry BuildRegistry()
    {
        var loader = new ArticleScenarioYamlLoader();
        var stub = new StubEval();
        var scenarioBuilder = new ScenarioToAtomicEval(stub, judgeModel: "stub");
        var builder = new ArticleCompositeBuilder(scenarioBuilder);
        return new ArticlesRegistry(loader, builder);
    }

    private sealed class StubEval : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 100, Summary = "stub" });
    }

    // ── G6.8 / constructor tests ───────────────────────────────────────────

    [Fact]
    public void Constructor_AcceptsNullRegistry_Optional()
    {
        // Arrange / Act
        var renderer = new GDPRPdfRenderer(articles: null);

        // Assert — no exception; null registry is explicitly supported
        Assert.NotNull(renderer);
    }

    [Fact]
    public void Constructor_AcceptsRegistryInstance()
    {
        // Arrange
        var registry = BuildRegistry();

        // Act
        var renderer = new GDPRPdfRenderer(registry);

        // Assert
        Assert.NotNull(renderer);
    }

    // ── Argument-validation tests ──────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_NullEvidence_Throws()
    {
        // Arrange
        var renderer = new GDPRPdfRenderer();
        var outputPath = Path.Combine(_tempDir, "report.pdf");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => renderer.RenderAsync(null!, outputPath));
    }

    [Fact]
    public async Task RenderAsync_NullOutputPath_Throws()
    {
        // Arrange
        var renderer = new GDPRPdfRenderer();
        var evidence = MakeMinimalEvidence();

        // Act & Assert — ArgumentNullException is a subclass of ArgumentException; accept either
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => renderer.RenderAsync(evidence, null!));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task RenderAsync_WhitespaceOutputPath_Throws()
    {
        // Arrange
        var renderer = new GDPRPdfRenderer();
        var evidence = MakeMinimalEvidence();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => renderer.RenderAsync(evidence, "   "));
    }

    // ── Integration / smoke tests ──────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_GeneratesNonEmptyPdfFile()
    {
        // Arrange
        var renderer = new GDPRPdfRenderer();
        var evidence = MakeMinimalEvidence();
        var outputPath = Path.Combine(_tempDir, "report.pdf");

        // Act
        await renderer.RenderAsync(evidence, outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), $"Expected PDF file at {outputPath}");
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 0, "PDF file should be non-empty");
        // PDF files start with %PDF
        var header = new byte[4];
        await using var fs = File.OpenRead(outputPath);
        _ = await fs.ReadAsync(header);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
    }

    [Fact]
    public async Task RenderAsync_WithRegistry_GeneratesNonEmptyPdfFile()
    {
        // Arrange
        var registry = BuildRegistry();
        var renderer = new GDPRPdfRenderer(registry);
        var evidence = MakeMinimalEvidence();
        var outputPath = Path.Combine(_tempDir, "report-with-registry.pdf");

        // Act
        await renderer.RenderAsync(evidence, outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    [Fact]
    public async Task RenderAsync_CreatesOutputDirectory_IfNotExisting()
    {
        // Arrange
        var renderer = new GDPRPdfRenderer();
        var evidence = MakeMinimalEvidence();
        var nestedDir = Path.Combine(_tempDir, "nested", "sub", "dir");
        var outputPath = Path.Combine(nestedDir, "report.pdf");

        // Act
        await renderer.RenderAsync(evidence, outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task RenderAsync_SmokePreset_EmptyPillars_GeneratesValidPdf()
    {
        // Arrange — smoke preset has no per-pillar entries
        var renderer = new GDPRPdfRenderer();
        var evidence = MakeMinimalEvidence(preset: "smoke", hasPillars: false);
        var outputPath = Path.Combine(_tempDir, "smoke-report.pdf");

        // Act
        await renderer.RenderAsync(evidence, outputPath);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }
}
