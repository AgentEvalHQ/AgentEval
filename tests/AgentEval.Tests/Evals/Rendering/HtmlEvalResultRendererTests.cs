// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals.Rendering;

/// <summary>
/// Tests for <see cref="HtmlEvalResultRenderer"/> — v0.10.1 Phase A. Verifies the renderer
/// emits valid self-contained HTML with proper escaping, severity color-coding, skipped-leaf
/// honesty, and configurable provenance disclosure.
/// </summary>
public class HtmlEvalResultRendererTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EvalResult MakeAtomic(
        string key, double value, string label, bool passed, string severity,
        string? evidence = null, string evaluatorType = "atomic-code",
        string? judgeModel = null)
    {
        return new EvalResult(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: null,
                Evidence: evidence is null ? null : new[] { new EvalEvidence("test", "ref:1", evidence) },
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new(evaluatorType, judgeModel, null, null, null, 0.001, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult MakeComposite(string key, double value, string label, bool passed, params EvalResult[] children) =>
        new(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, passed, null, passed ? "none" : "high", null),
            Details: new(null, null, null, children, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResultRenderOptions DefaultOpts(string? title = null, bool includeProvenance = true, string? auditHash = null) =>
        new(
            Subject: new SubjectIdentity(SubjectKind.Agent, "TestAgent"),
            Title: title,
            RegulationOrBenchmark: "Test Suite",
            RunId: "run-001",
            GeneratedAt: new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero),
            IncludeProvenance: includeProvenance,
            AuditHash: auditHash,
            AgentEvalVersion: "0.10.1-beta");

    private static string Render(EvalResult result, EvalResultRenderOptions opts)
    {
        var bytes = new HtmlEvalResultRenderer().RenderAsync(result, opts).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(bytes);
    }

    // ── Format-id / extension contract ────────────────────────────────────────

    [Fact]
    public void Format_id_and_extension_are_html()
    {
        var r = new HtmlEvalResultRenderer();
        Assert.Equal("html", r.FormatId);
        Assert.Equal(".html", r.FileExtension);
    }

    // ── Smoke: simple atomic result ───────────────────────────────────────────

    [Fact]
    public void Renders_simple_atomic_result()
    {
        var result = MakeAtomic("smoke-1", 0.85, "pass", passed: true, severity: "none");
        var html = Render(result, DefaultOpts(title: "Simple Smoke"));

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<title>Simple Smoke</title>", html);
        Assert.Contains("TestAgent", html);
        Assert.Contains("Test Suite", html);
        Assert.Contains("run-001", html);
        // Score badge appears
        Assert.Contains("85.0", html);
    }

    // ── Deep composite tree ───────────────────────────────────────────────────

    [Fact]
    public void Renders_deep_composite_tree_three_levels()
    {
        var leaf1 = MakeAtomic("leaf-a", 0.9, "pass", true, "none");
        var leaf2 = MakeAtomic("leaf-b", 0.4, "fail", false, "high");
        var inner = MakeComposite("inner", 0.65, "warn", false, leaf1, leaf2);
        var root = MakeComposite("root", 0.65, "warn", false, inner);

        var html = Render(root, DefaultOpts());

        // All three keys appear
        Assert.Contains("leaf-a", html);
        Assert.Contains("leaf-b", html);
        Assert.Contains("inner", html);
        Assert.Contains("root", html);
        // Severity styling appears
        Assert.Contains("sev-high", html);
        Assert.Contains("sev-pass", html);
    }

    // ── XSS escaping ──────────────────────────────────────────────────────────

    [Fact]
    public void Encodes_user_content_to_prevent_xss()
    {
        var result = new EvalResult(
            Metric: new("<script>bad()</script>", "Inject<script>", "test", "1.0.0"),
            Score: new(0.5, null, "warn", false, null, "medium", null),
            Details: new(
                null,
                new[] { new EvalEvidence("<source>", "<ref>", "alert('xss')<script>boom</script>") },
                Recommendations: new[] { "<img src=x onerror=alert(1)>" },
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var html = Render(result, DefaultOpts());

        Assert.DoesNotContain("<script>bad()</script>", html);
        Assert.DoesNotContain("<script>boom</script>", html);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    // ── Skipped leaves rendered honestly ──────────────────────────────────────

    [Fact]
    public void Skipped_leaves_render_as_not_tested()
    {
        var skipped = EvalResult.Skipped(
            new TestEval(), reason: "Azure OpenAI credentials missing");

        var html = Render(skipped, DefaultOpts());
        Assert.Contains("NOT TESTED", html);
        Assert.Contains("sev-skipped", html);
        // Skipped scenarios are visually distinct (grey) — no fake-pass green class.
        Assert.DoesNotContain("<span class=\"badge sev-pass\">", html);
    }

    private sealed class TestEval : AgentEval.Evals.IEval
    {
        public string Key => "skip-leaf";
        public string Name => "Skipped Test";
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(EvalResult.Skipped(this, "skipped"));
    }

    // ── Audit hash + version footer ───────────────────────────────────────────

    [Fact]
    public void Audit_hash_appears_in_footer_when_provided()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        var html = Render(result, DefaultOpts(auditHash: "sha256:abcdef1234567890"));

        Assert.Contains("Audit hash", html);
        Assert.Contains("sha256:abcdef1234567890", html);
        Assert.Contains("0.10.1-beta", html);
    }

    [Fact]
    public void Audit_hash_omitted_when_null()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        var html = Render(result, DefaultOpts(auditHash: null));

        Assert.DoesNotContain("Audit hash", html);
    }

    // ── IncludeProvenance toggle ──────────────────────────────────────────────

    [Fact]
    public void Provenance_section_appears_when_enabled()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none", judgeModel: "gpt-4o-judge");
        var html = Render(result, DefaultOpts(includeProvenance: true));

        Assert.Contains("Provenance", html);
        Assert.Contains("gpt-4o-judge", html);
    }

    [Fact]
    public void Provenance_section_omitted_when_disabled()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none", judgeModel: "gpt-4o-judge");
        var html = Render(result, DefaultOpts(includeProvenance: false));

        Assert.DoesNotContain("gpt-4o-judge", html);
        // Header may still mention the word — check for the section header specifically
        Assert.DoesNotContain("<h4>Provenance</h4>", html);
    }

    // ── Self-containment ──────────────────────────────────────────────────────

    [Fact]
    public void Output_is_self_contained_no_external_references()
    {
        var result = MakeAtomic("k", 0.9, "pass", true, "none");
        var html = Render(result, DefaultOpts());

        // No external assets — stylesheet is inline, no external links / scripts / images
        Assert.DoesNotContain("<link ", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img ", html);
        Assert.Contains("<style>", html);
    }

    // ── UTF-8 round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void Output_is_utf8_encoded()
    {
        var result = new EvalResult(
            Metric: new("k", "Naïve résumé café 漢字", "test", "1.0.0"),
            Score: new(0.9, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var bytes = new HtmlEvalResultRenderer().RenderAsync(result, DefaultOpts()).GetAwaiter().GetResult();
        var s = Encoding.UTF8.GetString(bytes);
        // WebUtility.HtmlEncode escapes Latin-extended chars to numeric entities but leaves
        // higher-BMP characters (CJK) untouched. Either way the content survives the encode +
        // UTF-8 round-trip; we just check both the encoded Latin entities and the literal CJK glyphs.
        Assert.Contains("Na&#239;ve", s);
        Assert.Contains("r&#233;sum&#233;", s);
        Assert.Contains("caf&#233;", s);
        Assert.Contains("漢字", s);
    }
}
