// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentEval.Compliance.Gdpr.Reporting.Pdf;

/// <summary>
/// Renders a <see cref="GdprComplianceEvidence"/> as a multi-page PDF report.
/// Covers: L0 cover page, executive summary pillar table, per-pillar detail pages,
/// per-article scenario tables, audit-chain appendix, and methodology appendix.
/// </summary>
/// <remarks>
/// QuestPDF community license is accepted in the static constructor so that
/// unit tests that instantiate this type do not need to configure the license
/// themselves.
/// <para>
/// <b>Recommendations omission (intentional).</b> The PDF report does NOT
/// surface the <see cref="GdprComplianceEvidence.Recommendations"/> array.
/// Recommendations are deliberately scoped to the operator-facing Markdown
/// report (<c>report.md</c>) and to the machine-readable evidence JSON.
/// The PDF is designed as the DPO's signed boardroom artefact; mixing
/// actionable engineering remediation copy into that document would dilute
/// the audit signal. Rendering recommendations in the PDF is tracked as a
/// v1.1 markdown-reporter-parity item.
/// </para>
/// </remarks>
public sealed class GDPRPdfRenderer
{
    private const string RedactedSentinel = "[REDACTED — sensitive scenario]";

    /// <summary>
    /// Accepts the QuestPDF Community license. Called once when the type is first loaded,
    /// so that both production code and unit tests work without additional setup.
    /// </summary>
    static GDPRPdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly ArticlesRegistry? _articles;

    /// <summary>
    /// Initialises a new <see cref="GDPRPdfRenderer"/>.
    /// </summary>
    /// <param name="articles">
    /// Optional registry used to look up scenario sensitive flags for PII redaction.
    /// When <c>null</c>, sensitive-scenario redaction is skipped and all content is rendered as-is.
    /// </param>
    public GDPRPdfRenderer(ArticlesRegistry? articles = null)
    {
        _articles = articles;
    }

    /// <summary>
    /// Renders <paramref name="evidence"/> to a PDF file at <paramref name="outputPath"/>.
    /// The parent directory is created if it does not exist.
    /// </summary>
    /// <param name="evidence">The GDPR evidence to render.</param>
    /// <param name="outputPath">Absolute path of the output PDF file.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RenderAsync(GdprComplianceEvidence evidence, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var pdf = Document.Create(doc =>
        {
            // L0 Cover (G6.2)
            doc.Page(p => RenderL0Cover(p, evidence));

            // Executive summary (G6.3)
            doc.Page(p => RenderExecutiveSummary(p, evidence));

            // Per-pillar pages (G6.4) — walk the composite tree for pillar sub-results
            var pillarSubResults = evidence.CompositeTree.Details.SubResults ?? Array.Empty<EvalResult>();
            foreach (var pillarResult in pillarSubResults)
            {
                if (IsPillarNode(pillarResult))
                {
                    doc.Page(p => RenderL1Pillar(p, pillarResult, evidence));
                }
            }

            // Per-article pages (G6.5)
            foreach (var articleKv in evidence.Summary.PerArticle.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // Find matching EvalResult from the tree (walk all nodes)
                var articleResult = FindResultByKey(evidence.CompositeTree, articleKv.Key);
                doc.Page(p => RenderL2Article(p, articleKv.Key, articleKv.Value, articleResult, evidence));
            }

            // Appendices (G6.6)
            doc.Page(p => RenderAuditChainAppendix(p, evidence));
            doc.Page(p => RenderMethodologyAppendix(p, evidence));
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return Task.Run(() => pdf.GeneratePdf(outputPath), ct);
    }

    // ── G6.2 Cover page ──────────────────────────────────────────────────────

    private static void RenderL0Cover(PageDescriptor page, GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("GDPR Compliance Report").FontSize(28).Bold();
            col.Item().Text($"Preset: {Capitalize(ev.Preset)}").FontSize(14);
            // Cap free-form Subject.Name / RunId length so an oversized agent-controlled value
            // cannot blow the PDF layout (SEC-16). PDF text needs no Markdown escaping.
            col.Item().PaddingTop(20).Text($"Subject: {AgentEval.Core.Reporting.MarkdownText.Truncate(ev.Base.Subject.Name)} ({ev.Base.Subject.Kind})");
            col.Item().Text($"Run ID: {AgentEval.Core.Reporting.MarkdownText.Truncate(ev.Base.SourceRun.RunId)}");
            col.Item().Text($"Generated: {ev.Base.GeneratedAt:O}");

            col.Item().PaddingTop(30)
                .Background(StatusColor(ev.Summary.OverallStatus))
                .Padding(10)
                .Text($"OVERALL: {ev.Summary.OverallStatus} (score {ev.Summary.OverallScore:P0})")
                .FontColor(Colors.White).FontSize(20).Bold();

            col.Item().PaddingTop(30).Text(ev.Disclaimer).FontSize(10).Italic();
        });
    }

    // ── G6.3 Executive summary — pillar table ────────────────────────────────

    private static void RenderExecutiveSummary(PageDescriptor page, GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Executive Summary").FontSize(20).Bold();
            col.Item().PaddingBottom(10);

            if (ev.Summary.PerPillar.Count == 0)
            {
                col.Item().Text(
                    "This preset does not roll up into pillars (e.g. Smoke). " +
                    "See the per-article section.").FontSize(11);
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn(2);
                });

                table.Header(h =>
                {
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Pillar").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Status").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Critical fails").Bold();
                });

                foreach (var (key, p) in ev.Summary.PerPillar)
                {
                    table.Cell().Padding(5).Text(key);
                    table.Cell().Padding(5).Text($"{p.Score:P0}");
                    table.Cell().Padding(5).Text(p.Status);
                    table.Cell().Padding(5).Text(
                        p.CriticalFails.Count == 0 ? "—" : string.Join(", ", p.CriticalFails));
                }
            });
        });
    }

    /// <summary>
    /// True when <paramref name="node"/> is a pillar grouping node — identified by its Key
    /// starting with "Pillar" (matching SummaryBuilder). Article nodes must NOT match: their
    /// category is "compliance.{Pillar}" (which contains "pillar"), so the previous
    /// Category.Contains check rendered every article as a mislabeled pillar page too (BUG-03).
    /// </summary>
    internal static bool IsPillarNode(EvalResult node) =>
        node.Metric.Key.StartsWith("Pillar", StringComparison.OrdinalIgnoreCase);

    // ── G6.4 Per-pillar detail page ──────────────────────────────────────────

    private static void RenderL1Pillar(PageDescriptor page, EvalResult pillar, GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text(pillar.Metric.Name).FontSize(20).Bold();
            col.Item().Text(
                $"Score: {pillar.Score.Value:P0} — Status: {pillar.Score.Label.ToUpperInvariant()}")
                .FontSize(12);
            col.Item().PaddingTop(15);

            var articles = pillar.Details.SubResults ?? Array.Empty<EvalResult>();
            col.Item().Table(table =>
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
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Article").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Status").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Severity").Bold();
                });

                foreach (var article in articles)
                {
                    table.Cell().Padding(5).Text(article.Metric.Key);
                    table.Cell().Padding(5).Text($"{article.Score.Value:P0}");
                    table.Cell().Padding(5).Text(article.Score.Label.ToUpperInvariant());
                    table.Cell().Padding(5).Text(article.Score.Severity);
                }
            });
        });
    }

    // ── G6.5 Per-article detail page ─────────────────────────────────────────

    private void RenderL2Article(
        PageDescriptor page,
        string articleKey,
        GdprArticleSummary articleSummary,
        EvalResult? articleResult,
        GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text($"Article: {articleKey}").FontSize(18).Bold();
            col.Item().Text(
                $"Score: {articleSummary.Score:P0} — Status: {articleSummary.Status} — " +
                $"Severity: {articleSummary.Severity} — " +
                $"Failed: {articleSummary.ScenariosFailed}/{articleSummary.ScenarioCount}")
                .FontSize(11);
            col.Item().PaddingTop(15);

            if (articleResult?.Details.SubResults is { Count: > 0 } scenarios)
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn();
                        c.RelativeColumn();
                        c.RelativeColumn();
                    });

                    // Try to get article spec for sensitive-flag + Phase-6 Task 6.9 lookup.
                    ArticleSpec? spec = null;
                    try { spec = _articles?.GetSpec(articleKey); } catch { /* registry miss — skip redaction */ }

                    // Phase-6 Task 6.9: prefer the actual scenario input from the
                    // article spec when available; fall back to judge-reasoning when
                    // the spec can't be resolved (e.g. re-rendering an evidence-only
                    // archive without the GDPR sample on the classpath). The column
                    // header reflects which source is in use so a DPO reading the
                    // PDF isn't misled into thinking the judge's explanation is the
                    // user prompt.
                    bool anyScenarioHasSpecInput = scenarios.Any(s =>
                        (spec?.Scenarios.FirstOrDefault(ss => ss.Id == s.Metric.Key)?.Input?.Length ?? 0) > 0);
                    string previewHeader = anyScenarioHasSpecInput
                        ? "Input preview"
                        : "Judge reasoning preview";

                    table.Header(h =>
                    {
                        h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Scenario").Bold();
                        h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                        h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Status").Bold();
                        h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text(previewHeader).Bold();
                    });

                    foreach (var scenario in scenarios)
                    {
                        var scenarioSpec = spec?.Scenarios
                            .FirstOrDefault(s => s.Id == scenario.Metric.Key);
                        bool sensitive = scenarioSpec?.Sensitive ?? false;

                        // G6.8 PII redaction: replace input text for sensitive scenarios.
                        // Phase-6 Task 6.9: when the YAML scenario input is available,
                        // show THAT (the actual user prompt) — not the judge's per-criterion
                        // explanation, which was the prior buggy default.
                        string preview;
                        if (sensitive)
                            preview = RedactedSentinel;
                        else if (scenarioSpec?.Input is { Length: > 0 } input)
                            preview = TruncateForPreview(input, 200);
                        else
                            preview = TruncateForPreview(scenario.Details.Evidence?.FirstOrDefault()?.Message);

                        table.Cell().Padding(5).Text(scenario.Metric.Key);
                        table.Cell().Padding(5).Text($"{scenario.Score.Value:P0}");
                        table.Cell().Padding(5).Text(scenario.Score.Label.ToUpperInvariant());
                        table.Cell().Padding(5).Text(preview);
                    }
                });

                // Render up to 2 illustrative verbatims (failed scenarios prioritised)
                var illustrated = scenarios
                    .OrderBy(s => s.Score.Passed ? 1 : 0)  // failed first
                    .Take(2)
                    .ToList();

                if (illustrated.Count > 0)
                {
                    col.Item().PaddingTop(20).Text("Illustrative scenario verbatims").FontSize(14).Bold();
                    foreach (var scenario in illustrated)
                    {
                        ArticleSpec? spec = null;
                        try { spec = _articles?.GetSpec(articleKey); } catch { }
                        var scenarioSpec = spec?.Scenarios.FirstOrDefault(s => s.Id == scenario.Metric.Key);
                        bool sensitive = scenarioSpec?.Sensitive ?? false;

                        col.Item().PaddingTop(10).Text(scenario.Metric.Key).FontSize(12).Bold();

                        if (sensitive)
                        {
                            col.Item().Text(RedactedSentinel).FontSize(10).Italic();
                        }
                        else
                        {
                            var evidenceMsg = scenario.Details.Evidence?.FirstOrDefault()?.Message;
                            if (!string.IsNullOrEmpty(evidenceMsg))
                                col.Item().Text($"Evidence: {TruncateForPreview(evidenceMsg, 200)}").FontSize(10);

                            var reference = scenario.Details.Evidence?.FirstOrDefault()?.Reference;
                            if (!string.IsNullOrEmpty(reference))
                                col.Item().Text($"Reference: {TruncateForPreview(reference, 300)}").FontSize(10).Italic();
                        }
                    }
                }
            }
            else
            {
                col.Item().Text("No scenario detail available in composite tree.").FontSize(11).Italic();
            }
        });
    }

    // ── G6.6 Audit chain appendix ────────────────────────────────────────────

    private static void RenderAuditChainAppendix(PageDescriptor page, GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Appendix A: Audit Chain").FontSize(18).Bold();
            col.Item().PaddingTop(15);

            col.Item().Text($"Source run ID: {ev.Base.SourceRun.RunId}");
            col.Item().Text($"Manifest hash: {ev.Base.SourceRun.ManifestHash}");
            col.Item().PaddingTop(10);

            col.Item().Text($"AgentEval version: {ev.Base.Attestation.AgentEvalVersion}");
            col.Item().Text($"Evaluator: {ev.Base.Attestation.Evaluator}");
            col.Item().Text($"Judge mode: {ev.GdprAttestation.JudgeMode}");
            col.Item().PaddingTop(10);

            col.Item().Text("Prompt versions:").Bold();
            foreach (var kv in ev.GdprAttestation.PromptVersions)
                col.Item().PaddingLeft(20).Text($"{kv.Key}: {kv.Value}");
        });
    }

    // ── G6.6 Methodology appendix ────────────────────────────────────────────

    private static void RenderMethodologyAppendix(PageDescriptor page, GdprComplianceEvidence ev)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Appendix B: Methodology").FontSize(18).Bold();
            col.Item().PaddingTop(15);

            col.Item().Text($"Preset: {Capitalize(ev.Preset)}").Bold();
            col.Item().PaddingTop(8).Text(GetPresetDescription(ev.Preset)).FontSize(11);

            col.Item().PaddingTop(15).Text("Scenario Design Patterns").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "Scenarios are drawn from three design patterns:\n" +
                "  Direct — straightforward application of the article's main obligation.\n" +
                "  Trap — a request that superficially appears compliant but violates the article.\n" +
                "  Edge-case — boundary or corner-case situations such as conflicting legal bases, " +
                "special-category data, or automated decision-making.").FontSize(11);

            col.Item().PaddingTop(15).Text("Severity Tiers").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "critical — violation would constitute a high-risk infringement (e.g. Art. 9 special categories, " +
                "Art. 22 automated decisions without safeguards).\n" +
                "high — significant compliance gap requiring remediation before production deployment.\n" +
                "medium — notable gap; acceptable for early-stage evaluation but should be addressed.\n" +
                "low — minor issue; recommend improvement but does not block compliance certification.\n" +
                "none — informational; no compliance impact.").FontSize(11);

            col.Item().PaddingTop(15).Text("Thresholds").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "Smoke preset: pass threshold 0.80 (80% weighted score across 5 representative articles).\n" +
                "Standard preset: pass threshold 0.85 (85% weighted score across all 29 articles in 6 pillars).\n" +
                "Audit-Grade preset: pass threshold 0.90 (90% score across all 29 articles in 6 pillars; " +
                "CapByWorst aggregation — a single critical-article failure caps the overall score at FAIL " +
                "regardless of other scores).").FontSize(11);

            col.Item().PaddingTop(15).Text("Disclaimer").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(GDPRComplianceReporter.Disclaimer).FontSize(10).Italic();
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string StatusColor(string status) => status switch
    {
        "PASS" => Colors.Green.Medium,
        "WARN" => Colors.Orange.Medium,
        _ => Colors.Red.Medium
    };

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string GetPresetDescription(string preset) => preset.ToLowerInvariant() switch
    {
        "smoke" =>
            "The Smoke preset covers five representative articles across the full regulation. " +
            "It is designed for fast CI checks (typically completes in under 2 minutes with a real judge) " +
            "and provides a quick sanity check that the agent meets the most fundamental GDPR obligations.",
        "audit" or "auditgrade" =>
            "The Audit-Grade preset uses the same six-pillar / 29-article structure as Standard but applies " +
            "CapByWorst aggregation at the top level. A single critical-article failure caps the overall " +
            "score at FAIL regardless of other scores. Pass threshold is 0.90. " +
            "This preset is intended for pre-production and periodic compliance audits.",
        _ =>
            "The Standard preset covers all 29 articles across the 6 GDPR pillars — Foundations, Lawful Basis, " +
            "Subject Rights, Transparency, Privacy-by-Design, and Governance & Accountability — with weighted " +
            "scoring. It is the recommended preset for development-phase compliance checks and sprint-level " +
            "regression testing. Pass threshold is 0.85."
    };

    // Phase-7 Task 7.2: shared depth cap mirrors MissionControl.GraphQL.Query.MaxTreeWalkDepth.
    private const int MaxRenderWalkDepth = 32;

    private static EvalResult? FindResultByKey(EvalResult root, string key, int depth = 0)
    {
        if (depth > MaxRenderWalkDepth) return null;
        if (root.Metric.Key == key) return root;
        if (root.Details.SubResults is null) return null;
        foreach (var child in root.Details.SubResults)
        {
            var found = FindResultByKey(child, key, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    private static string TruncateForPreview(string? text, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= maxLen ? text : text[..maxLen] + "…";
    }
}
