// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;
using Xunit;

namespace AgentEval.Tests.Rendering.Pdf;

/// <summary>
/// Tests for <see cref="PdfEvalResultRenderer"/> — v0.10.1 Phase B. Verifies the
/// renderer emits well-formed PDF bytes starting with the <c>%PDF-</c> signature,
/// handles atomic and deeply-nested composite trees, and honors the
/// <see cref="EvalResultRenderOptions.IncludeProvenance"/> toggle. No external
/// PDF parser is pulled in — we assert on the byte signature + non-zero size, which
/// is the cheapest reliable structural check.
/// </summary>
public class PdfEvalResultRendererTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EvalResult MakeAtomic(string key, double value, string label, bool passed, string severity, string? evidence = null) =>
        new(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: null,
                Evidence: evidence is null ? null : new[] { new EvalEvidence("test", "ref:1", evidence) },
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", "judge-gpt-4o", null, null, 100, 0.001, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult MakeComposite(string key, double value, string label, bool passed, params EvalResult[] children) =>
        new(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, passed, null, passed ? "none" : "high", null),
            Details: new(null, null, null, children, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResultRenderOptions DefaultOpts(bool includeProvenance = true) =>
        new(
            Subject: new SubjectIdentity(SubjectKind.Agent, "TestAgent"),
            Title: "Test PDF Report",
            RegulationOrBenchmark: "Test Suite",
            RunId: "run-pdf-001",
            GeneratedAt: new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero),
            IncludeProvenance: includeProvenance,
            AuditHash: "sha256:test-hash",
            AgentEvalVersion: "0.10.1-beta");

    // ── Format-id / extension contract ────────────────────────────────────────

    [Fact]
    public void FormatIdAndFileExtension_OnPdfRenderer_ArePdfValues()
    {
        var r = new PdfEvalResultRenderer();
        Assert.Equal("pdf", r.FormatId);
        Assert.Equal(".pdf", r.FileExtension);
    }

    // ── Smoke: simple atomic result ───────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_SimpleAtomicResult_ProducesValidPdfWithSignature()
    {
        var result = MakeAtomic("smoke-1", 0.85, "pass", true, "none", evidence: "Looks good.");
        var bytes = await new PdfEvalResultRenderer().RenderAsync(result, DefaultOpts());

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000, $"PDF unexpectedly small: {bytes.Length} bytes");
        // PDF byte signature: %PDF-
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal((byte)'-', bytes[4]);
    }

    // ── Deep composite tree ───────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_DeepCompositeTreeThreeLevels_ProducesValidPdf()
    {
        var leaf1 = MakeAtomic("leaf-a", 0.9, "pass", true, "none");
        var leaf2 = MakeAtomic("leaf-b", 0.4, "fail", false, "high", evidence: "Detected violation.");
        var inner = MakeComposite("inner-composite", 0.65, "warn", false, leaf1, leaf2);
        var root = MakeComposite("root-composite", 0.65, "warn", false, inner);

        var bytes = await new PdfEvalResultRenderer().RenderAsync(root, DefaultOpts());

        Assert.True(bytes.Length > 2000, $"Multi-page PDF unexpectedly small: {bytes.Length} bytes");
        // PDF signature check
        var head = Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.StartsWith("%PDF-", head);
    }

    // ── QuestPDF community license is auto-accepted via static ctor ───────────

    [Fact]
    public async Task StaticConstructor_OnFirstAccess_AcceptsQuestPdfCommunityLicense()
    {
        // If the license wasn't set we'd see a license-related exception. The
        // simplest assertion is that we can render without prior license setup.
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        var bytes = await new PdfEvalResultRenderer().RenderAsync(result, DefaultOpts());
        Assert.True(bytes.Length > 0);
    }

    // ── Skipped leaves render honestly ────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_SkippedLeaves_RenderedWithoutThrowing()
    {
        var skipped = new EvalResult(
            Metric: new("skip", "Skipped Test", "test", "1.0.0"),
            Score: new(0, null, "skipped", false, null, "none", null),
            Details: new(null, null, new[] { "skipped: cred missing" }, null, null),
            Provenance: new("skipped", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var bytes = await new PdfEvalResultRenderer().RenderAsync(skipped, DefaultOpts());
        Assert.True(bytes.Length > 0);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    // ── Provenance toggle ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_IncludeProvenanceFalse_StillProducesValidPdf()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        var bytes = await new PdfEvalResultRenderer().RenderAsync(result, DefaultOpts(includeProvenance: false));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    // ── Honors cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PdfEvalResultRenderer().RenderAsync(result, DefaultOpts(), cts.Token));
    }
}
