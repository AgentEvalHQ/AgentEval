// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentEval.Rendering.Pdf;

/// <summary>
/// Renders a generic <see cref="EvalResult"/> composite tree as an audit-grade PDF
/// using QuestPDF (Community license). Implements <see cref="IEvalResultRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// v0.10.1 Phase B. The renderer produces a four-section document:
/// </para>
/// <list type="number">
///   <item>Cover page — subject identity, benchmark/regulation framing, overall verdict.</item>
///   <item>Composite summary — first-level child breakdown (when the root is a composite).</item>
///   <item>Per-leaf detail pages — each atomic result with score, severity, provenance, evidence.</item>
///   <item>Audit chain appendix — audit hash, AgentEval version, generation timestamp.</item>
/// </list>
/// <para>
/// The QuestPDF Community license is accepted in the static constructor so that
/// callers and unit tests do not need to set it manually.
/// </para>
/// <para>
/// <b>Relationship to family-specific renderers</b>: <c>GDPRPdfRenderer</c>,
/// <c>EuAiActPdfRenderer</c>, and <c>AgenticPdfRenderer</c> continue to consume the
/// family-specific evidence envelopes (which carry pillar tables, attestation blocks,
/// methodology appendices) and remain the right choice for compliance-grade
/// boardroom reports. <see cref="PdfEvalResultRenderer"/> targets cross-family
/// scenarios — discovery walkthroughs, custom benchmarks, third-party plugins.
/// </para>
/// </remarks>
public sealed class PdfEvalResultRenderer : IEvalResultRenderer
{
    static PdfEvalResultRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <inheritdoc/>
    public string FormatId => "pdf";

    /// <inheritdoc/>
    public string FileExtension => ".pdf";

    private const int MaxRenderWalkDepth = EvalTreeLimits.MaxTreeWalkDepth; // ARC-03: one source of truth

    /// <inheritdoc/>
    public Task<byte[]> RenderAsync(EvalResult result, EvalResultRenderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested(); // honor cancellation requested before render (GAP-12)

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); // GAP-12 — don't start the synchronous render if cancelled
            var doc = Document.Create(d =>
            {
                d.Page(p => RenderCover(p, result, options));
                if (result.Details.SubResults is { Count: > 0 } children)
                {
                    d.Page(p => RenderComponentSummary(p, result, children, options));
                }

                // Walk all leaves (atomic results) and produce one detail page per leaf,
                // up to a depth cap that mirrors the GraphQL query's MaxTreeWalkDepth so
                // a pathological circular tree does not blow the page count.
                var leaves = CollectLeaves(result, maxDepth: MaxRenderWalkDepth).ToList();
                foreach (var leaf in leaves)
                {
                    d.Page(p => RenderLeafDetail(p, leaf, options));
                }

                d.Page(p => RenderAuditAppendix(p, result, options));
            });

            using var ms = new MemoryStream();
            doc.GeneratePdf(ms);
            return ms.ToArray();
        }, ct);
    }

    // ── Cover page ───────────────────────────────────────────────────────────

    private static void RenderCover(PageDescriptor page, EvalResult root, EvalResultRenderOptions opts)
    {
        page.Margin(40);
        page.Header().Text(opts.RegulationOrBenchmark ?? "AgentEval Report").FontSize(10).FontColor(Colors.Grey.Darken1);
        page.Footer().AlignCenter().Text(t =>
        {
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages();
        });

        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Text(opts.Title ?? root.Metric.Name).FontSize(28).Bold();
            if (!string.IsNullOrEmpty(opts.RegulationOrBenchmark))
                col.Item().Text(opts.RegulationOrBenchmark!).FontSize(14).FontColor(Colors.Grey.Darken1);

            col.Item().PaddingTop(20);
            col.Item().Text($"Subject: {opts.Subject.Name} ({opts.Subject.Kind})").FontSize(12);
            if (!string.IsNullOrEmpty(opts.Subject.ModelId))
                col.Item().Text($"Model: {opts.Subject.ModelId}").FontSize(11);
            if (!string.IsNullOrEmpty(opts.Subject.Framework))
                col.Item().Text($"Framework: {opts.Subject.Framework}").FontSize(11);
            if (!string.IsNullOrEmpty(opts.RunId))
                col.Item().Text($"Run ID: {opts.RunId}").FontSize(11);

            var generatedAt = opts.GeneratedAt ?? DateTimeOffset.UtcNow;
            col.Item().Text($"Generated: {generatedAt:O}").FontSize(11);
            col.Item().Text($"Scenarios: {CountLeaves(root, 0)}").FontSize(11);

            col.Item().PaddingTop(28);
            var sevColor = SeverityColor(root.Score.Severity, root.Score.Label);
            var label = string.Equals(root.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase)
                ? "NOT TESTED"
                : root.Score.Label.ToUpperInvariant();
            col.Item().Background(sevColor).Padding(12)
                .Text($"OVERALL: {label} ({root.Score.Value:P0})")
                .FontColor(Colors.White).FontSize(20).Bold();

            if (!string.IsNullOrEmpty(opts.AgentEvalVersion))
                col.Item().PaddingTop(20).Text($"AgentEval version: {opts.AgentEvalVersion}").FontSize(9).Italic();
        });
    }

    // ── Composite component summary ──────────────────────────────────────────

    private static void RenderComponentSummary(
        PageDescriptor page, EvalResult root, IReadOnlyList<EvalResult> children, EvalResultRenderOptions opts)
    {
        page.Margin(40);
        StandardHeaderFooter(page, opts);

        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Text("Component Summary").FontSize(20).Bold();
            col.Item().Text("First-level children of the composite tree.").FontSize(11).FontColor(Colors.Grey.Darken1);

            col.Item().PaddingTop(10).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                });

                table.Header(h =>
                {
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Component").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Label").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Severity").Bold();
                });

                foreach (var child in children)
                {
                    var isSkipped = string.Equals(child.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase);
                    table.Cell().Padding(5).Text(child.Metric.Key);
                    table.Cell().Padding(5).Text(isSkipped ? "—" : child.Score.Value.ToString("P0"));
                    table.Cell().Padding(5).Text(isSkipped ? "NOT TESTED" : child.Score.Label.ToUpperInvariant());
                    table.Cell().Padding(5).Text(child.Score.Severity ?? "none");
                }
            });
        });
    }

    // ── Per-leaf detail page ─────────────────────────────────────────────────

    private static void RenderLeafDetail(PageDescriptor page, EvalResult leaf, EvalResultRenderOptions opts)
    {
        page.Margin(40);
        StandardHeaderFooter(page, opts);

        page.Content().Column(col =>
        {
            col.Spacing(6);
            col.Item().Text(leaf.Metric.Name).FontSize(18).Bold();
            col.Item().Text($"Key: {leaf.Metric.Key}  •  Category: {leaf.Metric.Category}  •  v{leaf.Metric.Version}")
                .FontSize(10).FontColor(Colors.Grey.Darken1);

            var isSkipped = string.Equals(leaf.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase);
            col.Item().PaddingTop(8).Text(
                isSkipped
                    ? "Status: NOT TESTED"
                    : $"Score: {leaf.Score.Value:P1}  •  Label: {leaf.Score.Label.ToUpperInvariant()}  •  Severity: {leaf.Score.Severity}")
                .FontSize(12);

            if (opts.IncludeProvenance)
            {
                col.Item().PaddingTop(10).Text("Provenance").FontSize(13).Bold();
                col.Item().Text($"Evaluator: {leaf.Provenance.Type}").FontSize(10);
                if (!string.IsNullOrEmpty(leaf.Provenance.JudgeModel))
                    col.Item().Text($"Judge: {leaf.Provenance.JudgeModel}").FontSize(10);
                if (!string.IsNullOrEmpty(leaf.Provenance.PromptId))
                    col.Item().Text($"Prompt ID: {leaf.Provenance.PromptId}").FontSize(10);
                if (leaf.Provenance.TokensUsed is { } tokens)
                    col.Item().Text($"Tokens: {tokens}").FontSize(10);
                if (leaf.Provenance.EstimatedCost > 0)
                    col.Item().Text($"Est. cost (USD): {leaf.Provenance.EstimatedCost:F4}").FontSize(10);
                col.Item().Text($"Cache hit: {(leaf.Provenance.CacheHit ? "yes" : "no")}").FontSize(10);
            }

            if (leaf.Details.Dimensions is { Count: > 0 } dims)
            {
                col.Item().PaddingTop(10).Text("Metrics").FontSize(13).Bold();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2);
                        c.RelativeColumn();
                    });
                    foreach (var (k, v) in dims)
                    {
                        t.Cell().Padding(3).Text(k).FontSize(10);
                        t.Cell().Padding(3).Text(v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)).FontSize(10);
                    }
                });
            }

            if (leaf.Details.Evidence is { Count: > 0 } evidence)
            {
                col.Item().PaddingTop(10).Text("Evidence").FontSize(13).Bold();
                // Plan-13 T4.1b item 8 (F-PDF-1): truncation length is now operator-configurable
                // via opts.EvidenceTruncationLength (default 800). When truncation fires we
                // append a "(N more lines)" footer per evidence entry so the reader knows
                // exactly how much was dropped. Set EvidenceTruncationLength = 0 to disable.
                var maxLen = opts.EvidenceTruncationLength;
                foreach (var ev in evidence)
                {
                    var msg = ev.Message ?? string.Empty;
                    string? overflowFooter = null;
                    if (maxLen > 0 && msg.Length > maxLen)
                    {
                        var truncated = msg[..maxLen];
                        var droppedTail = msg[maxLen..];
                        // Approx "lines dropped" — counts \n in the dropped tail. When the dropped
                        // tail is a single very-long line, we surface the character count instead.
                        var droppedLines = droppedTail.Count(c => c == '\n');
                        overflowFooter = droppedLines > 0
                            ? $"… (truncated; {droppedLines} more line{(droppedLines == 1 ? "" : "s")}, {droppedTail.Length:N0} more chars)"
                            : $"… (truncated; {droppedTail.Length:N0} more chars)";
                        msg = truncated;
                    }
                    col.Item().Text(t =>
                    {
                        if (!string.IsNullOrEmpty(ev.Source))
                            t.Span($"[{ev.Source}] ").Bold();
                        t.Span(msg);
                        if (!string.IsNullOrEmpty(ev.Reference))
                            t.Span($"  ({ev.Reference})").Italic();
                    });
                    if (overflowFooter is not null)
                    {
                        col.Item().Text(overflowFooter)
                            .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
                    }
                }
            }

            if (leaf.Details.Recommendations is { Count: > 0 } recs)
            {
                col.Item().PaddingTop(10).Text("Recommendations").FontSize(13).Bold();
                foreach (var r in recs)
                    col.Item().Text($"• {r}").FontSize(10);
            }
        });
    }

    // ── Audit appendix ───────────────────────────────────────────────────────

    private static void RenderAuditAppendix(PageDescriptor page, EvalResult root, EvalResultRenderOptions opts)
    {
        page.Margin(40);
        StandardHeaderFooter(page, opts);

        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Text("Appendix: Audit Trail").FontSize(18).Bold();
            col.Item().PaddingTop(8);

            if (!string.IsNullOrEmpty(opts.AuditHash))
                col.Item().Text($"Audit hash: {opts.AuditHash}").FontSize(11);
            if (!string.IsNullOrEmpty(opts.RunId))
                col.Item().Text($"Run ID: {opts.RunId}").FontSize(11);
            if (!string.IsNullOrEmpty(opts.AgentEvalVersion))
                col.Item().Text($"AgentEval version: {opts.AgentEvalVersion}").FontSize(11);

            var generatedAt = opts.GeneratedAt ?? DateTimeOffset.UtcNow;
            col.Item().Text($"Generated: {generatedAt:O}").FontSize(11);
            col.Item().Text($"Root key: {root.Metric.Key}").FontSize(11);
            col.Item().Text($"Root evaluator: {root.Provenance.Type}").FontSize(11);
            col.Item().Text($"Leaf count: {CountLeaves(root, 0)}").FontSize(11);

            col.Item().PaddingTop(20).Text(
                "This report was produced by AgentEval and reflects the deterministic scoring " +
                "of the underlying evaluators. It does not constitute legal advice.")
                .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void StandardHeaderFooter(PageDescriptor page, EvalResultRenderOptions opts)
    {
        page.Header().Text(opts.RegulationOrBenchmark ?? "AgentEval Report")
            .FontSize(9).FontColor(Colors.Grey.Darken1);
        page.Footer().AlignCenter().Text(t =>
        {
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages();
        });
    }

    private static string SeverityColor(string? severity, string? label)
    {
        if (string.Equals(label, "skipped", StringComparison.OrdinalIgnoreCase))
            return Colors.Grey.Medium;

        // Verdict label outranks severity for badge / cover colour: a composite that
        // misses its threshold ("fail") with all-low-severity leaves would otherwise
        // render green on the cover. Treat fail/warn labels as the colour anchor; fall
        // back to severity for pass / unlabelled cases.
        if (string.Equals(label, "fail", StringComparison.OrdinalIgnoreCase))
            return Colors.Red.Medium;
        if (string.Equals(label, "warn", StringComparison.OrdinalIgnoreCase))
            return Colors.Orange.Medium;

        return severity?.ToLowerInvariant() switch
        {
            "critical" or "high" => Colors.Red.Medium,
            "medium" => Colors.Orange.Medium,
            "low" or "none" => Colors.Green.Medium,
            _ => Colors.Green.Medium,
        };
    }

    private static int CountLeaves(EvalResult node, int depth)
    {
        if (depth > MaxRenderWalkDepth) return 1;
        if (node.Details.SubResults is not { Count: > 0 } children) return 1;
        var total = 0;
        foreach (var c in children) total += CountLeaves(c, depth + 1);
        return total;
    }

    private static IEnumerable<EvalResult> CollectLeaves(EvalResult node, int maxDepth, int depth = 0)
    {
        if (depth > maxDepth) yield break;
        if (node.Details.SubResults is not { Count: > 0 } children)
        {
            yield return node;
            yield break;
        }
        foreach (var c in children)
            foreach (var leaf in CollectLeaves(c, maxDepth, depth + 1))
                yield return leaf;
    }
}
