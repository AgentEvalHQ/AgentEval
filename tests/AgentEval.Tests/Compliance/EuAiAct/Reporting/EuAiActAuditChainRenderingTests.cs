// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct.Reporting;

/// <summary>
/// The EU AI Act Markdown and PDF reports must not claim an audit-chain check they did not run.
/// Both renderers only have the hash copied into the evidence, never the source run it points at.
/// The Markdown report used to print <c>VALID</c> for any non-empty hash and <c>BROKEN</c> for an
/// empty one; the PDF appendix printed the hash alone, or an empty string. These tests fail on that
/// rendering and pass on the honest one: "hash recorded, not verified in this report" plus how to
/// verify it, or "no hash recorded".
/// </summary>
// Rendered PDFs are read back as text. Under a fully parallel run the text came back as NUL characters (seen once
// on net8.0, never alone), so these render outside the parallel phase, with the other tests that touch QuestPDF's
// process-wide state.
[Collection(AgentEval.Tests.Rendering.Pdf.QuestPdfLicenceCollection.Name)]
public class EuAiActAuditChainRenderingTests : IDisposable
{
    private readonly string _tempDir;

    public EuAiActAuditChainRenderingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agenteval-euai-chain-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Markdown ─────────────────────────────────────────────────────────────

    [Fact]
    public void Markdown_RecordedHash_IsNotVerified_NeverValid()
    {
        var md = new MarkdownRenderer().Render(MakeEvidence("sha256:abc123"));

        Assert.Contains("## Audit Chain", md, StringComparison.Ordinal);
        Assert.Contains("**Manifest hash**: `sha256:abc123`", md, StringComparison.Ordinal);
        Assert.DoesNotContain("VALID", md, StringComparison.Ordinal);
        Assert.DoesNotContain("BROKEN", md, StringComparison.Ordinal);
        Assert.Contains("**Chain status**: hash recorded, **not verified** in this report", md, StringComparison.Ordinal);
        Assert.Contains("`agenteval doctor`", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Markdown_NoHash_SaysNoHashRecorded_NotBroken(string manifestHash)
    {
        var md = new MarkdownRenderer().Render(MakeEvidence(manifestHash));

        Assert.DoesNotContain("BROKEN", md, StringComparison.Ordinal);
        Assert.DoesNotContain("VALID", md, StringComparison.Ordinal);
        Assert.Contains("**Manifest hash**: —", md, StringComparison.Ordinal);
        Assert.Contains("**Chain status**: **no hash recorded**", md, StringComparison.Ordinal);
        Assert.DoesNotContain("agenteval doctor", md, StringComparison.Ordinal);
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PdfAuditChainLines_RecordedHash_SaysNotVerified_AndHowToVerify()
    {
        var lines = EuAiActPdfRenderer.AuditChainLines(new SourceRunRef("run-eu-001", "sha256:abc"));

        Assert.Equal(
            new[]
            {
                "Manifest hash: sha256:abc",
                "Chain status: hash recorded, not verified in this report.",
            },
            lines.Take(2));
        Assert.Contains("'agenteval doctor'", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("VALID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PdfAuditChainLines_NoHash_SaysNoHashRecorded(string manifestHash)
    {
        var lines = EuAiActPdfRenderer.AuditChainLines(new SourceRunRef("run-eu-001", manifestHash));

        Assert.Equal(
            new[]
            {
                "Manifest hash: —",
                "Chain status: no hash recorded, so this evidence cannot be checked against its source run.",
            },
            lines);
    }

    [Fact]
    public async Task Pdf_RecordedHash_AppendixSaysNotVerifiedInThisReport()
    {
        var outputPath = Path.Combine(_tempDir, "chain-recorded.pdf");
        await new EuAiActPdfRenderer().RenderAsync(MakeEvidence("sha256:" + new string('b', 64)), outputPath);

        var text = ExtractTextWithoutWhitespace(outputPath);

        // Whitespace is stripped because PDF text extraction does not promise to keep word spacing or
        // line wraps; fragments avoid "fi"/"fl" so a typographic ligature cannot split them.
        Assert.Contains("hashrecorded,not", text, StringComparison.Ordinal);
        Assert.Contains("inthisreport", text, StringComparison.Ordinal);
        Assert.Contains("agentevaldoctor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nohashrecorded", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_NoHash_AppendixSaysNoHashRecorded()
    {
        var outputPath = Path.Combine(_tempDir, "chain-absent.pdf");
        await new EuAiActPdfRenderer().RenderAsync(MakeEvidence(""), outputPath);

        var text = ExtractTextWithoutWhitespace(outputPath);

        Assert.Contains("nohashrecorded", text, StringComparison.Ordinal);
        Assert.Contains("cannotbechecked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("agentevaldoctor", text, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractTextWithoutWhitespace(string pdfPath)
    {
        var sb = new StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(File.ReadAllBytes(pdfPath));
        foreach (var page in doc.GetPages())
            sb.Append(page.Text);
        return new string(sb.ToString().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static EuAiActComplianceEvidence MakeEvidence(string manifestHash)
    {
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: EuAiActComplianceReporter.Regulation,
            Subject: new SubjectIdentity(SubjectKind.Agent, "EuChainTestAgent"),
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef("run-eu-001", manifestHash),
            Controls: [],
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "AgentEval.Compliance.EuAiAct", "stub"));

        var leaf = new EvalResult(
            Metric: new("eu_ai.art50.disclosure-001", "Art.50 scenario", "compliance.eu_ai", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var article = new EvalResult(
            Metric: new("eu_ai.art50.disclosure", "AI interaction disclosure", "compliance.eu_ai", "1.0"),
            Score: new(0.90, null, "pass", true, 0.75, "high", null),
            Details: new(null, null, null, [leaf], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var root = new EvalResult(
            Metric: new("eu_ai.compliance.smoke", "EU AI Act Compliance", "compliance.eu_ai", "1.0"),
            Score: new(0.90, null, "pass", true, 0.80, "none", null),
            Details: new(null, null, null, [article], "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new EuAiActComplianceEvidence(
            Base: baseEvidence,
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: root,
            Summary: new EuAiActSummary(
                OverallScore: 0.90,
                OverallStatus: "PASS",
                PerPillar: new Dictionary<string, EuAiActPillarSummary>(),
                PerArticle: new Dictionary<string, EuAiActArticleSummary>
                {
                    ["eu_ai.art50.disclosure"] = new(0.90, "PASS", 1, 0, "high")
                }),
            CriticalFindings: [],
            Recommendations: Array.Empty<AgentEval.Compliance.Core.Recommendation>(),
            Disclaimer: EuAiActComplianceReporter.Disclaimer,
            EuAiActAttestation: new EuAiActAttestation(
                "mode-a",
                new Dictionary<string, string> { ["eu-ai-act-judge-system"] = "v1" }));
    }
}
