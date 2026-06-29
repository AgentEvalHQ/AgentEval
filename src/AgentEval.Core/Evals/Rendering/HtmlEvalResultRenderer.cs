// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using AgentEval.Evals;

namespace AgentEval.Core.Evals.Rendering;

/// <summary>
/// Renders a generic <see cref="EvalResult"/> composite tree as a self-contained HTML
/// document — single file, inline CSS, no JavaScript, no external assets. The output is
/// safe to email, drop into a GitHub gist, or serve from a static-site host.
/// </summary>
/// <remarks>
/// <para>
/// v0.10.1 Phase A. The renderer walks the composite tree top-down and emits each node
/// as an HTML <c>&lt;details&gt;</c>/<c>&lt;summary&gt;</c> pair so a reviewer can drill
/// into the report progressively. All user-supplied content (metric names, evidence
/// messages, judge reasoning) is HTML-encoded through <see cref="WebUtility.HtmlEncode(string?)"/>
/// before being written out — XSS-safe even when run against adversarial prompts.
/// </para>
/// <para>
/// Severity color-coding: <b>red</b> for high/critical, <b>amber</b> for medium, <b>green</b>
/// for low/passed, and <b>grey</b> for skipped leaves (rendered honestly as "Not Tested" —
/// no silent up-coding).
/// </para>
/// </remarks>
public sealed class HtmlEvalResultRenderer : IEvalResultRenderer
{
    /// <inheritdoc/>
    public string FormatId => "html";

    /// <inheritdoc/>
    public string FileExtension => ".html";

    /// <inheritdoc/>
    public Task<byte[]> RenderAsync(EvalResult result, EvalResultRenderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder(8192);
        WriteDocument(sb, result, options);
        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ── Document scaffolding ──────────────────────────────────────────────────

    private static void WriteDocument(StringBuilder sb, EvalResult root, EvalResultRenderOptions opts)
    {
        var title = WebUtility.HtmlEncode(opts.Title ?? root.Metric.Name);
        var generatedAt = opts.GeneratedAt ?? DateTimeOffset.UtcNow;

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head>\n");
        sb.Append("<meta charset=\"utf-8\"/>\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>\n");
        sb.Append("<title>").Append(title).Append("</title>\n");
        WriteStyles(sb);
        sb.Append("</head><body>\n");

        // Cover header
        sb.Append("<header class=\"cover\">\n");
        sb.Append("<h1>").Append(title).Append("</h1>\n");
        if (!string.IsNullOrEmpty(opts.RegulationOrBenchmark))
            sb.Append("<p class=\"subtitle\">").Append(WebUtility.HtmlEncode(opts.RegulationOrBenchmark)).Append("</p>\n");

        sb.Append("<dl class=\"meta\">\n");
        WriteMetaPair(sb, "Subject", $"{opts.Subject.Name} ({opts.Subject.Kind})");
        if (!string.IsNullOrEmpty(opts.Subject.ModelId))
            WriteMetaPair(sb, "Model", opts.Subject.ModelId!);
        if (!string.IsNullOrEmpty(opts.Subject.Framework))
            WriteMetaPair(sb, "Framework", opts.Subject.Framework!);
        if (!string.IsNullOrEmpty(opts.RunId))
            WriteMetaPair(sb, "Run ID", opts.RunId!);
        WriteMetaPair(sb, "Generated", generatedAt.ToString("O"));

        var leafCount = CountLeaves(root);
        WriteMetaPair(sb, "Scenarios", leafCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("</dl>\n");

        // Overall verdict banner — translate "skipped" to "NOT TESTED" so an atomic
        // skipped root presents consistently with the leaf badges (which already do
        // this remapping inside WriteNode) and with the PDF cover ("OVERALL: NOT
        // TESTED"). Without this, the HTML cover label read "SKIPPED" while per-leaf
        // badges read "NOT TESTED" for the same state. Score is kept visible (mirrors
        // PDF cover behaviour); for an atomic skipped result this is 0%.
        var sev = SeverityClass(root.Score.Severity, root.Score.Label);
        var rootIsSkipped = string.Equals(root.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase);
        var rootBannerLabel = rootIsSkipped ? "NOT TESTED" : WebUtility.HtmlEncode(root.Score.Label.ToUpperInvariant());
        sb.Append("<div class=\"verdict ").Append(sev).Append("\">\n");
        sb.Append("<span class=\"label\">").Append(rootBannerLabel).Append("</span>\n");
        sb.Append("<span class=\"score\">").Append(FormatPct(root.Score.Value)).Append("</span>\n");
        sb.Append("</div>\n");

        sb.Append("</header>\n");

        // Tree body
        sb.Append("<main>\n");
        WriteNode(sb, root, opts, depth: 0, isRoot: true);
        sb.Append("</main>\n");

        // Footer with audit + version traceability
        sb.Append("<footer>\n");
        if (!string.IsNullOrEmpty(opts.AuditHash))
            sb.Append("<p><strong>Audit hash:</strong> <code>").Append(WebUtility.HtmlEncode(opts.AuditHash!)).Append("</code></p>\n");
        if (!string.IsNullOrEmpty(opts.AgentEvalVersion))
            sb.Append("<p><strong>AgentEval version:</strong> ").Append(WebUtility.HtmlEncode(opts.AgentEvalVersion!)).Append("</p>\n");
        sb.Append("<p class=\"disclaimer\">Generated by AgentEval. This report reflects the deterministic ");
        sb.Append("scoring of the underlying evaluators and does not constitute legal advice.</p>\n");
        sb.Append("</footer>\n");

        sb.Append("</body></html>\n");
    }

    // ── Recursive node renderer ──────────────────────────────────────────────

    private static void WriteNode(StringBuilder sb, EvalResult node, EvalResultRenderOptions opts, int depth, bool isRoot)
    {
        var sev = SeverityClass(node.Score.Severity, node.Score.Label);
        var isSkipped = string.Equals(node.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase);

        // Open at depth 0..1 by default for readability; deeper levels stay collapsed.
        var openAttr = isRoot || depth <= 1 ? " open" : string.Empty;

        sb.Append("<details class=\"node ").Append(sev);
        if (isSkipped) sb.Append(" skipped");
        sb.Append("\"").Append(openAttr).Append(">\n");

        sb.Append("<summary>\n");
        sb.Append("<span class=\"badge ").Append(sev).Append("\">");
        sb.Append(isSkipped ? "NOT TESTED" : WebUtility.HtmlEncode(node.Score.Label.ToUpperInvariant()));
        sb.Append("</span>\n");
        sb.Append("<span class=\"name\">").Append(WebUtility.HtmlEncode(node.Metric.Name)).Append("</span>\n");
        sb.Append("<span class=\"key\">").Append(WebUtility.HtmlEncode(node.Metric.Key)).Append("</span>\n");
        if (!isSkipped)
        {
            sb.Append("<span class=\"score-inline\">").Append(FormatPct(node.Score.Value)).Append("</span>\n");
        }
        sb.Append("</summary>\n");

        sb.Append("<div class=\"body\">\n");

        // Metadata row
        sb.Append("<dl class=\"node-meta\">\n");
        WriteMetaPair(sb, "Category", node.Metric.Category);
        WriteMetaPair(sb, "Version", node.Metric.Version);
        if (!isSkipped && node.Score.Threshold is { } th)
            WriteMetaPair(sb, "Threshold", FormatPct(th));
        WriteMetaPair(sb, "Severity", node.Score.Severity);
        if (node.Score.Confidence is { } cf)
            WriteMetaPair(sb, "Confidence", FormatPct(cf));
        WriteMetaPair(sb, "Evaluated", node.EvaluatedAt.ToString("O"));
        sb.Append("</dl>\n");

        // Provenance
        if (opts.IncludeProvenance)
        {
            sb.Append("<div class=\"provenance\">\n");
            sb.Append("<h4>Provenance</h4>\n<dl>\n");
            WriteMetaPair(sb, "Evaluator", node.Provenance.Type);
            if (!string.IsNullOrEmpty(node.Provenance.JudgeModel))
                WriteMetaPair(sb, "Judge", node.Provenance.JudgeModel!);
            if (!string.IsNullOrEmpty(node.Provenance.PromptId))
                WriteMetaPair(sb, "Prompt", node.Provenance.PromptId!);
            if (node.Provenance.TokensUsed is { } tokens)
                WriteMetaPair(sb, "Tokens", tokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (node.Provenance.EstimatedCost > 0)
                WriteMetaPair(sb, "Est. cost (USD)", node.Provenance.EstimatedCost.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            WriteMetaPair(sb, "Cache hit", node.Provenance.CacheHit ? "yes" : "no");
            sb.Append("</dl>\n</div>\n");
        }

        // Dimensions (numeric breakdowns)
        if (node.Details.Dimensions is { Count: > 0 } dims)
        {
            sb.Append("<div class=\"dimensions\">\n<h4>Metrics</h4>\n<table>\n<thead><tr><th>Name</th><th>Value</th></tr></thead>\n<tbody>\n");
            foreach (var (k, v) in dims)
            {
                sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(k)).Append("</td><td>")
                  .Append(v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            }
            sb.Append("</tbody></table>\n</div>\n");
        }

        // Evidence
        if (node.Details.Evidence is { Count: > 0 } evidence)
        {
            sb.Append("<div class=\"evidence\">\n<h4>Evidence</h4>\n<ul>\n");
            foreach (var ev in evidence)
            {
                sb.Append("<li>");
                if (!string.IsNullOrEmpty(ev.Source))
                    sb.Append("<strong>").Append(WebUtility.HtmlEncode(ev.Source)).Append(":</strong> ");
                sb.Append(WebUtility.HtmlEncode(ev.Message ?? string.Empty));
                if (!string.IsNullOrEmpty(ev.Reference))
                    sb.Append(" <span class=\"ref\">[").Append(WebUtility.HtmlEncode(ev.Reference)).Append("]</span>");
                sb.Append("</li>\n");
            }
            sb.Append("</ul>\n</div>\n");
        }

        // Recommendations
        if (node.Details.Recommendations is { Count: > 0 } recs)
        {
            sb.Append("<div class=\"recommendations\">\n<h4>Recommendations</h4>\n<ul>\n");
            foreach (var r in recs)
                sb.Append("<li>").Append(WebUtility.HtmlEncode(r)).Append("</li>\n");
            sb.Append("</ul>\n</div>\n");
        }

        // Skipped reason — render honestly so reviewers can spot under-coverage.
        if (isSkipped && node.Details.Recommendations is null && node.Details.Evidence is null)
        {
            sb.Append("<p class=\"skipped-note\">This scenario was skipped and is reported as <em>Not Tested</em>. ");
            sb.Append("Skipped leaves never contribute to a pass verdict.</p>\n");
        }

        // Sub-results — recurse.
        if (node.Details.SubResults is { Count: > 0 } children)
        {
            sb.Append("<div class=\"children\">\n");
            foreach (var child in children)
                WriteNode(sb, child, opts, depth + 1, isRoot: false);
            sb.Append("</div>\n");
        }

        sb.Append("</div>\n</details>\n");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void WriteMetaPair(StringBuilder sb, string label, string value)
    {
        sb.Append("<dt>").Append(WebUtility.HtmlEncode(label)).Append("</dt>");
        sb.Append("<dd>").Append(WebUtility.HtmlEncode(value)).Append("</dd>\n");
    }

    private static string FormatPct(double v) =>
        v.ToString("P1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps the (severity, label) pair onto one of the four CSS classes used by the
    /// stylesheet. Treats <c>skipped</c> specially so leaves render honestly.
    /// </summary>
    private static string SeverityClass(string? severity, string? label)
    {
        if (string.Equals(label, "skipped", StringComparison.OrdinalIgnoreCase))
            return "sev-skipped";

        // An "error" leaf is an evaluation-infrastructure failure (severity "none"), not a measured
        // result — render it neutral (like skipped) so it never reads as a green pass. Mirrors the
        // PDF renderer, which greys "error" for the same reason.
        if (string.Equals(label, "error", StringComparison.OrdinalIgnoreCase))
            return "sev-skipped";

        // Verdict label outranks severity for the badge color: a composite that misses
        // its threshold ("fail") with all-low-severity leaves would otherwise render
        // green. Treat any explicit fail/warn label as the colour anchor; fall back to
        // severity for unlabelled / pass cases.
        if (string.Equals(label, "fail", StringComparison.OrdinalIgnoreCase))
            return "sev-high";
        if (string.Equals(label, "warn", StringComparison.OrdinalIgnoreCase))
            return "sev-medium";

        return severity?.ToLowerInvariant() switch
        {
            "critical" or "high" => "sev-high",
            "medium"             => "sev-medium",
            "low"                => "sev-low",
            "none"               => "sev-pass",
            _                    => "sev-pass",
        };
    }

    /// <summary>Counts atomic (leaf) results in the tree for the cover-header summary line.</summary>
    private static int CountLeaves(EvalResult node)
    {
        if (node.Details.SubResults is not { Count: > 0 } children) return 1;
        var total = 0;
        foreach (var c in children) total += CountLeaves(c);
        return total;
    }

    // ── Inline stylesheet ────────────────────────────────────────────────────

    private static void WriteStyles(StringBuilder sb)
    {
        sb.Append("<style>\n");
        sb.Append(@"
:root {
  --fg: #1f2328; --muted: #57606a; --bg: #ffffff;
  --pass-bg: #dafbe1; --pass-fg: #1a7f37; --pass-border: #1f7c34;
  --med-bg: #fff8c5;  --med-fg:  #9a6700; --med-border:  #bf8700;
  --high-bg: #ffebe9; --high-fg: #cf222e; --high-border: #a40e26;
  --skip-bg: #f6f8fa; --skip-fg: #57606a; --skip-border: #afb8c1;
  --low-bg:  #dafbe1; --low-fg:  #1a7f37; --low-border:  #1f7c34;
}
* { box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  font-size: 14px; line-height: 1.5;
  color: var(--fg); background: var(--bg);
  margin: 0; padding: 24px; max-width: 1200px;
}
header.cover { border-bottom: 1px solid #d0d7de; padding-bottom: 16px; margin-bottom: 24px; }
h1 { margin: 0 0 4px; font-size: 24px; }
.subtitle { color: var(--muted); margin: 0 0 16px; font-size: 16px; }
dl.meta { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; margin: 12px 0; font-size: 13px; }
dl.meta dt { font-weight: 600; color: var(--muted); }
dl.meta dd { margin: 0; }
.verdict { display: inline-flex; gap: 8px; align-items: center; padding: 8px 16px; border-radius: 6px; font-weight: 700; margin-top: 8px; border: 1px solid; }
.verdict .label { font-size: 16px; letter-spacing: 0.5px; }
.verdict .score { font-size: 18px; font-family: ui-monospace, monospace; }
.verdict.sev-pass    { background: var(--pass-bg); color: var(--pass-fg); border-color: var(--pass-border); }
.verdict.sev-low     { background: var(--low-bg);  color: var(--low-fg);  border-color: var(--low-border);  }
.verdict.sev-medium  { background: var(--med-bg);  color: var(--med-fg);  border-color: var(--med-border);  }
.verdict.sev-high    { background: var(--high-bg); color: var(--high-fg); border-color: var(--high-border); }
.verdict.sev-skipped { background: var(--skip-bg); color: var(--skip-fg); border-color: var(--skip-border); }
details.node { border: 1px solid #d0d7de; border-radius: 6px; margin: 6px 0; padding: 0; }
details.node[open] { background: #fafbfc; }
summary { padding: 8px 12px; cursor: pointer; display: flex; align-items: center; gap: 8px; list-style: none; }
summary::-webkit-details-marker { display: none; }
summary::before { content: '▶'; transition: transform 0.1s; color: var(--muted); display: inline-block; width: 12px; font-size: 10px; }
details[open] > summary::before { transform: rotate(90deg); }
.badge { padding: 2px 8px; border-radius: 999px; font-size: 11px; font-weight: 700; letter-spacing: 0.3px; border: 1px solid; }
.badge.sev-pass    { background: var(--pass-bg); color: var(--pass-fg); border-color: var(--pass-border); }
.badge.sev-low     { background: var(--low-bg);  color: var(--low-fg);  border-color: var(--low-border);  }
.badge.sev-medium  { background: var(--med-bg);  color: var(--med-fg);  border-color: var(--med-border);  }
.badge.sev-high    { background: var(--high-bg); color: var(--high-fg); border-color: var(--high-border); }
.badge.sev-skipped { background: var(--skip-bg); color: var(--skip-fg); border-color: var(--skip-border); }
.name { font-weight: 600; flex: 1; }
.key { color: var(--muted); font-family: ui-monospace, monospace; font-size: 12px; }
.score-inline { font-family: ui-monospace, monospace; font-size: 13px; color: var(--muted); }
.body { padding: 8px 16px 12px; border-top: 1px solid #eaeef2; }
.children { margin-top: 8px; padding-left: 12px; border-left: 2px solid #eaeef2; }
dl.node-meta { display: grid; grid-template-columns: max-content 1fr; gap: 2px 10px; font-size: 12px; margin: 4px 0; color: var(--muted); }
dl.node-meta dt { font-weight: 600; }
dl.node-meta dd { margin: 0; }
.provenance, .evidence, .recommendations, .dimensions { margin-top: 8px; }
.provenance h4, .evidence h4, .recommendations h4, .dimensions h4 { margin: 6px 0 4px; font-size: 12px; text-transform: uppercase; color: var(--muted); letter-spacing: 0.4px; }
.provenance dl { display: grid; grid-template-columns: max-content 1fr; gap: 2px 10px; font-size: 12px; margin: 0; }
.provenance dt { font-weight: 600; color: var(--muted); }
.provenance dd { margin: 0; font-family: ui-monospace, monospace; }
.dimensions table { border-collapse: collapse; font-size: 12px; }
.dimensions th, .dimensions td { padding: 2px 8px; text-align: left; border-bottom: 1px solid #eaeef2; }
.evidence ul, .recommendations ul { margin: 0; padding-left: 20px; font-size: 13px; }
.evidence .ref { color: var(--muted); font-style: italic; font-size: 11px; }
.skipped-note { font-size: 12px; color: var(--muted); font-style: italic; margin: 6px 0; }
footer { margin-top: 32px; padding-top: 12px; border-top: 1px solid #d0d7de; font-size: 12px; color: var(--muted); }
footer code { font-family: ui-monospace, monospace; background: #f6f8fa; padding: 1px 4px; border-radius: 3px; }
footer .disclaimer { font-style: italic; }
");
        sb.Append("</style>\n");
    }
}
