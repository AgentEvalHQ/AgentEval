// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Cli.Commands;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval compliance render</c> subcommand (<see cref="ComplianceRenderCommand"/>).
/// </summary>
public class ComplianceRenderCommandTests : IDisposable
{
    private readonly string _root;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ComplianceRenderCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-render-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<string> WriteFixtureEvidenceAsync(string subjectName)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sanitizedSubject = subjectName.Replace(' ', '-');
        var evidenceDir = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", sanitizedSubject, ts);
        Directory.CreateDirectory(evidenceDir);

        var evidence = MakeFixtureEvidence(subjectName);
        var evidenceFile = Path.Combine(evidenceDir, "gdpr-evidence.json");
        await using var stream = File.Create(evidenceFile);
        await JsonSerializer.SerializeAsync(stream, evidence, s_jsonOpts);

        return ts;
    }

    private static GdprComplianceEvidence MakeFixtureEvidence(string subjectName)
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "GDPR",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("run-render-001", "sha256:" + new string('b', 64)),
            Controls: [],
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval.GdprBenchmark", "stub"));

        var articleLeaf = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.art17.erasure-001", "scenario", "compliance", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var articleComposite = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.art17.erasure", "Erasure", "compliance", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, [articleLeaf], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var compositeTree = new AgentEval.Evals.EvalResult(
            Metric: new("gdpr.compliance.smoke", "GDPR Compliance Smoke", "compliance.gdpr", "1.0"),
            Score: new(0.90, null, "pass", true, 0.80, "none", null),
            Details: new(null, null, null, [articleComposite], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: new GdprSummary(
                OverallScore: 0.90,
                OverallStatus: "PASS",
                PerPillar: new Dictionary<string, GdprPillarSummary>(),
                PerArticle: new Dictionary<string, GdprArticleSummary>
                {
                    ["gdpr.art17.erasure"] = new(0.90, "PASS", 1, 0, "high")
                }),
            CriticalFindings: [],
            Recommendations: [],
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation(
                "mode-a",
                new Dictionary<string, string> { ["gdpr-judge-system"] = "v1" }));
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

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComplianceRender_UnsupportedRegulation_ReturnsExitCode1()
    {
        // Arrange / Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "sox",
            subject: "AnyAgent",
            ts: null,
            rootOverride: _root,
            registryOverride: null);

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ComplianceRender_MissingEvidenceDirectory_ReturnsExitCode1()
    {
        // Arrange — .agenteval/compliance/GDPR/<subject> does not exist

        // Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: "NonExistentAgent",
            ts: null,
            rootOverride: _root,
            registryOverride: null);

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ComplianceRender_ValidEvidence_ProducesPdfFile()
    {
        // Arrange
        const string subjectName = "RenderTestAgent";
        var ts = await WriteFixtureEvidenceAsync(subjectName);
        var registry = BuildRegistry();

        // Act
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: subjectName,
            ts: ts,
            rootOverride: _root,
            registryOverride: registry);

        // Assert
        Assert.Equal(0, exitCode);

        var evidenceDir = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", subjectName, ts);
        var pdfPath = Path.Combine(evidenceDir, "report.pdf");
        Assert.True(File.Exists(pdfPath), $"Expected PDF at {pdfPath}");
        Assert.True(new FileInfo(pdfPath).Length > 0, "PDF file should be non-empty");
    }

    [Fact]
    public async Task ComplianceRender_OmittedTs_UsesLatestDirectory()
    {
        // Arrange — write two evidence snapshots; command should find the latest
        const string subjectName = "AutoLatestAgent";
        await WriteFixtureEvidenceAsync(subjectName);
        await Task.Delay(1100); // ensure different second-resolution timestamps
        var latestTs = await WriteFixtureEvidenceAsync(subjectName);
        var registry = BuildRegistry();

        // Act — ts is null; should auto-select the most recent
        var exitCode = await ComplianceRenderCommand.RunAsync(
            regulation: "gdpr",
            subject: subjectName,
            ts: null,
            rootOverride: _root,
            registryOverride: registry);

        // Assert
        Assert.Equal(0, exitCode);

        // Verify the PDF was written in the latest ts directory
        var pdfPath = Path.Combine(
            _root, ".agenteval", "compliance", "GDPR", subjectName, latestTs, "report.pdf");
        Assert.True(File.Exists(pdfPath), $"Expected PDF at {pdfPath}");
    }
}
