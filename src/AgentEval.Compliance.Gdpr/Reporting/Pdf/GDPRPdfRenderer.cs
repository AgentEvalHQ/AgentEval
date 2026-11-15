// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core.Reporting;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Reporting;
using AgentEval.Rendering.Pdf;

namespace AgentEval.Compliance.Gdpr.Reporting.Pdf;

/// <summary>Projects rich GDPR evidence and renders through an explicitly registered PDF provider.</summary>
public sealed class GDPRPdfRenderer(ArticlesRegistry? articles = null, IPdfRendererRegistry? pdfRenderers = null, string? providerId = null)
{
    /// <summary>Creates an unconfigured facade without ambiguity between optional constructor overloads.</summary>
    public GDPRPdfRenderer() : this(null, null, null) { }

    /// <summary>Preserves the original article-registry signature. Rendering requires an explicitly supplied PDF registry.</summary>
    public GDPRPdfRenderer(ArticlesRegistry? articles = null) : this(articles, null, null) { }

    /// <summary>Explicitly renders a PDF through the selected provider and atomically writes the destination.</summary>
    public Task RenderAsync(GdprComplianceEvidence evidence, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ct.ThrowIfCancellationRequested();
        var provider = PdfReportFiles.RequireProvider(pdfRenderers, providerId);
        return PdfReportFiles.RenderAsync(provider, CreateDocument(evidence), outputPath, cancellationToken: ct);
    }

    /// <summary>Projects all report facts and applies scenario redaction before any provider sees content.</summary>
    public ReportDocument CreateDocument(GdprComplianceEvidence ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var sections = new List<ReportSection>
        {
            new("GDPR Compliance — " + Capitalize(ev.Preset) + " Preset", [
                $"Subject: {ev.Base.Subject.Name} ({ev.Base.Subject.Kind})",
                $"Run ID: {ev.Base.SourceRun.RunId}", $"Generated: {ev.Base.GeneratedAt:O}",
                $"OVERALL: {ev.Summary.OverallStatus} (score {ev.Summary.OverallScoreText()})",
                $"Domain packs: {string.Join(", ", ev.DomainPacks)}", ev.Disclaimer]),
            new("Executive Summary", tables: [new(["Pillar", "Score", "Status", "Critical fails"],
                ev.Summary.PerPillar.Select(p => new[] { p.Key, ScoreFormatting.Percentage(p.Value.Score), p.Value.Status, string.Join(", ", p.Value.CriticalFails) }))]),
        };
        foreach (var (articleKey, summary) in ev.Summary.PerArticle.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var articleResult = EvalReportHelpers.FindByKey(ev.CompositeTree, articleKey);
            var spec = articles?.All.ContainsKey(articleKey) == true ? articles.GetSpec(articleKey) : null;
            var paragraphs = new List<string> { $"Score: {ScoreFormatting.Percentage(summary.Score)} — Status: {summary.Status} — Severity: {summary.Severity} — Failed: {summary.ScenariosFailed}/{summary.ScenarioCount}" };
            var rows = new List<string[]>();
            var scenarios = articleResult?.Details.SubResults ?? [];
            foreach (var scenario in scenarios)
            {
                var scenarioSpec = spec?.Scenarios.FirstOrDefault(s => s.Id == scenario.Metric.Key);
                var sensitive = scenarioSpec?.Sensitive == true;
                var preview = scenarioSpec is null ? "[WITHHELD — sensitivity metadata unavailable]"
                    : sensitive ? "[REDACTED — sensitive scenario]" : EvalReportHelpers.TruncateForPreview(
                    scenarioSpec?.Input ?? scenario.Details.Evidence?.FirstOrDefault()?.Message, 200);
                rows.Add([scenario.Metric.Key, ScoreFormatting.Percentage(scenario.Score.MeasuredValue()), scenario.Score.Label.ToUpperInvariant(), preview]);
            }
            foreach (var scenario in scenarios.OrderBy(s => s.Score.Passed ? 1 : 0).Take(2))
            {
                paragraphs.Add("Illustrative scenario: " + scenario.Metric.Key);
                var scenarioSpec = spec?.Scenarios.FirstOrDefault(s => s.Id == scenario.Metric.Key);
                if (scenarioSpec is null)
                    paragraphs.Add("[WITHHELD — sensitivity metadata unavailable]");
                else if (scenarioSpec.Sensitive)
                    paragraphs.Add("[REDACTED — sensitive scenario]");
                else
                    foreach (var item in scenario.Details.Evidence ?? [])
                    {
                        paragraphs.Add("Evidence: " + EvalReportProjection.TruncateEvidence(item.Message ?? "", 200));
                        if (!string.IsNullOrEmpty(item.Reference)) paragraphs.Add("Reference: " + EvalReportProjection.TruncateEvidence(item.Reference, 300));
                    }
            }
            if (scenarios.Count == 0) paragraphs.Add("No scenario detail available in composite tree.");
            var header = scenarios.Any(s => !string.IsNullOrEmpty(spec?.Scenarios.FirstOrDefault(ss => ss.Id == s.Metric.Key)?.Input))
                ? "Input preview" : "Judge reasoning preview";
            sections.Add(new("Article: " + articleKey, paragraphs, [new(["Scenario", "Score", "Status", header], rows)]));
        }
        sections.Add(new("Critical findings", ev.CriticalFindings.Select(f => $"{f.Metric.Key}: {ScoreFormatting.Percentage(f.Score.MeasuredValue())}; severity {f.Score.Severity}")));
        sections.Add(new("Recommendations", ev.Recommendations.Select(r => $"{r.ControlId} [{r.Severity}]: {r.Text}")));
        var audit = new List<string> { $"Source run ID: {ev.Base.SourceRun.RunId}" };
        audit.AddRange(AuditChainLines(ev.Base.SourceRun));
        audit.AddRange([$"AgentEval version: {ev.Base.Attestation.AgentEvalVersion}", $"Evaluator: {ev.Base.Attestation.Evaluator}", $"Judge mode: {ev.GdprAttestation.JudgeMode}"]);
        audit.AddRange(ev.GdprAttestation.PromptVersions.Select(p => $"{p.Key}: {p.Value}"));
        sections.Add(new("Appendix A: Audit Chain", audit));
        sections.Add(Methodology(ev));
        return new("GDPR Compliance Report", sections, ev.Base.GeneratedAt, ev.Base.Subject.Name, "AgentEval");
    }

    internal static bool IsPillarNode(EvalResult node) => node.Metric.Key.StartsWith("Pillar", StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<string> AuditChainLines(AgentEval.Output.SourceRunRef sourceRun) => EvalReportProjection.AuditChainLines(sourceRun);
    private static string Capitalize(string value) => EvalReportHelpers.Capitalize(value);
    private static ReportSection Methodology(GdprComplianceEvidence ev)
    {
        var paragraphs = new List<string>();
        paragraphs.Add("Appendix B: Methodology");
        paragraphs.Add($"Preset: {Capitalize(ev.Preset)}");
        paragraphs.Add(GetPresetDescription(ev.Preset));
        paragraphs.Add("Scenario Design Patterns");
        paragraphs.Add("Scenarios are drawn from three design patterns:\n" +
                "  Direct — straightforward application of the article's main obligation.\n" +
                "  Trap — a request that superficially appears compliant but violates the article.\n" +
                "  Edge-case — boundary or corner-case situations such as conflicting legal bases, " +
                "special-category data, or automated decision-making.");
        paragraphs.Add("Severity Tiers");
        paragraphs.Add("critical — violation would constitute a high-risk infringement (e.g. Art. 9 special categories, " +
                "Art. 22 automated decisions without safeguards).\n" +
                "high — significant compliance gap requiring remediation before production deployment.\n" +
                "medium — notable gap; acceptable for early-stage evaluation but should be addressed.\n" +
                "low — minor issue; recommend improvement but does not block compliance certification.\n" +
                "none — informational; no compliance impact.");
        paragraphs.Add("Thresholds");
        paragraphs.Add("Smoke preset: pass threshold 0.80 (80% weighted score across 5 representative articles).\n" +
                "Standard preset: pass threshold 0.85 (85% weighted score across all 29 articles in 6 pillars).\n" +
                "Audit-Grade preset: pass threshold 0.90 (90% score across all 29 articles in 6 pillars; " +
                "CapByWorst aggregation — a single critical-article failure caps the overall score at FAIL " +
                "regardless of other scores).");
        paragraphs.Add("Disclaimer");
        paragraphs.Add(GDPRComplianceReporter.Disclaimer);
        return new("Appendix B: Methodology", paragraphs.Skip(1));
    }

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
}
