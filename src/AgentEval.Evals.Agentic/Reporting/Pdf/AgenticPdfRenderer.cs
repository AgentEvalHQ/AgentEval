// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgentEval.Evals.Agentic.Reporting.Pdf;

/// <summary>
/// Renders an <see cref="AgenticBenchmarkResult"/> as a multi-page PDF report.
/// Covers: L0 cover page, executive summary category table, per-category detail pages,
/// per-evaluator failure pages, audit-chain appendix, and methodology appendix.
/// </summary>
/// <remarks>
/// QuestPDF community license is accepted in the static constructor so that
/// unit tests that instantiate this type do not need to configure the license
/// themselves.
/// </remarks>
public sealed class AgenticPdfRenderer
{
    /// <summary>
    /// Ordered list of well-known agentic benchmark categories for table presentation.
    /// Categories not in this list are appended alphabetically after the known ones.
    /// </summary>
    private static readonly string[] s_categoryOrder =
    [
        "system-outcome",
        "agentic-process",
        "rag",
        "safety-security",
        "operational",
        "judge-quality"
    ];

    /// <summary>
    /// Accepts the QuestPDF Community license. Called once when the type is first loaded,
    /// so that both production code and unit tests work without additional setup.
    /// </summary>
    static AgenticPdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Renders <paramref name="result"/> to a PDF file at <paramref name="outputPath"/>.
    /// The parent directory is created if it does not exist.
    /// </summary>
    /// <param name="result">The agentic benchmark result to render.</param>
    /// <param name="outputPath">Absolute path of the output PDF file.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RenderAsync(AgenticBenchmarkResult result, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ct.ThrowIfCancellationRequested(); // honor cancellation requested before render (GAP-12)

        var pdf = Document.Create(doc =>
        {
            // L0 Cover page
            doc.Page(p => RenderL0Cover(p, result));

            // Executive summary — per-category table
            doc.Page(p => RenderExecutiveSummary(p, result));

            // Per-category pages — one per non-empty category
            foreach (var (categoryKey, _) in OrderedCategories(result.Summary.PerCategory))
            {
                // Find evaluators belonging to this category
                var evaluatorsInCategory = result.Summary.PerEvaluator
                    .Where(kv => GetEvaluatorCategory(kv.Key, result.CompositeTree) == categoryKey)
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToList();

                doc.Page(p => RenderL1Category(p, categoryKey, result, evaluatorsInCategory));
            }

            // L2 Evaluator pages — one per failed or warned evaluator
            var failedOrWarnedEvaluators = result.Summary.PerEvaluator
                .Where(kv => kv.Value.Status is "FAIL" or "WARN")
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var (evalKey, evalSummary) in failedOrWarnedEvaluators)
            {
                var evalResult = FindResultByKey(result.CompositeTree, evalKey);
                doc.Page(p => RenderL2Evaluator(p, evalKey, evalSummary, evalResult));
            }

            // Appendices
            doc.Page(p => RenderAuditChainAppendix(p, result));
            doc.Page(p => RenderMethodologyAppendix(p, result));
        });

        ct.ThrowIfCancellationRequested(); // GAP-12
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); // GAP-12 — don't start the synchronous render if cancelled
            pdf.GeneratePdf(outputPath);
        }, ct);
    }

    // ── L0 Cover page ────────────────────────────────────────────────────────

    private static void RenderL0Cover(PageDescriptor page, AgenticBenchmarkResult result)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Agentic Benchmark Report").FontSize(28).Bold();
            col.Item().Text($"Preset: {FormatPresetTitle(result.Preset)} Preset").FontSize(14);
            col.Item().PaddingTop(20).Text($"Subject: {result.Subject.Name} ({result.Subject.Kind})");
            col.Item().Text($"Run ID: {result.SourceRunId}");
            col.Item().Text($"Generated: {result.GeneratedAt:O}");

            col.Item().PaddingTop(30)
                .Background(StatusColor(result.Summary.OverallStatus))
                .Padding(10)
                .Text($"OVERALL: {result.Summary.OverallStatus} (score {result.Summary.OverallScore:P0})")
                .FontColor(Colors.White).FontSize(20).Bold();

            col.Item().PaddingTop(30).Text(result.Disclaimer).FontSize(10).Italic();
        });
    }

    // ── Executive summary — per-category table ───────────────────────────────

    private static void RenderExecutiveSummary(PageDescriptor page, AgenticBenchmarkResult result)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Executive Summary").FontSize(20).Bold();
            col.Item().PaddingBottom(10);

            if (result.Summary.PerCategory.Count == 0)
            {
                col.Item().Text(
                    "This preset does not roll up into categories (flat preset). " +
                    "See the per-evaluator section.").FontSize(11);
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
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Category").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Status").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Critical fails").Bold();
                });

                foreach (var (key, cat) in OrderedCategories(result.Summary.PerCategory))
                {
                    table.Cell().Padding(5).Text(key);
                    table.Cell().Padding(5).Text($"{cat.Score:P0}");
                    table.Cell().Padding(5).Text(cat.Status);
                    table.Cell().Padding(5).Text(
                        cat.CriticalFails.Count == 0 ? "—" : string.Join(", ", cat.CriticalFails));
                }
            });

            // Overall summary row below the table
            col.Item().PaddingTop(20)
                .Background(StatusColor(result.Summary.OverallStatus))
                .Padding(8)
                .Text($"Overall: {result.Summary.OverallStatus} — {result.Summary.OverallScore:P0}")
                .FontColor(Colors.White).FontSize(14).Bold();
        });
    }

    // ── L1 Category page ─────────────────────────────────────────────────────

    private static void RenderL1Category(
        PageDescriptor page,
        string categoryKey,
        AgenticBenchmarkResult result,
        IReadOnlyList<KeyValuePair<string, AgenticEvaluatorSummary>> evaluators)
    {
        result.Summary.PerCategory.TryGetValue(categoryKey, out var cat);

        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text($"Category: {categoryKey}").FontSize(20).Bold();
            if (cat is not null)
            {
                col.Item().Text(
                    $"Score: {cat.Score:P0} — Status: {cat.Status.ToUpperInvariant()}")
                    .FontSize(12);
            }
            col.Item().PaddingTop(15);

            if (evaluators.Count == 0)
            {
                col.Item().Text("No evaluators mapped to this category.").FontSize(11).Italic();
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                });

                table.Header(h =>
                {
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Evaluator").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Score").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Status").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Severity").Bold();
                    h.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text("Confidence").Bold();
                });

                foreach (var (key, ev) in evaluators)
                {
                    var conf = ev.Confidence.HasValue ? $"{ev.Confidence.Value:P0}" : "—";
                    table.Cell().Padding(5).Text(key);
                    table.Cell().Padding(5).Text($"{ev.Score:P0}");
                    table.Cell().Padding(5).Text(ev.Status.ToUpperInvariant());
                    table.Cell().Padding(5).Text(ev.Severity);
                    table.Cell().Padding(5).Text(conf);
                }
            });
        });
    }

    // ── L2 Evaluator page ────────────────────────────────────────────────────

    private static void RenderL2Evaluator(
        PageDescriptor page,
        string evaluatorKey,
        AgenticEvaluatorSummary evalSummary,
        EvalResult? evalResult)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text($"Evaluator: {evaluatorKey}").FontSize(18).Bold();
            col.Item().Text(
                $"Score: {evalSummary.Score:P0} — Status: {evalSummary.Status.ToUpperInvariant()} — " +
                $"Severity: {evalSummary.Severity}")
                .FontSize(11);

            if (evalSummary.Confidence.HasValue)
                col.Item().Text($"Confidence: {evalSummary.Confidence.Value:P0}").FontSize(11);

            col.Item().PaddingTop(15);

            if (evalResult is null)
            {
                col.Item().Text("No detailed result available in composite tree.").FontSize(11).Italic();
                return;
            }

            var subResults = evalResult.Details.SubResults ?? Array.Empty<EvalResult>();
            col.Item().Text($"Sub-result count: {subResults.Count}").FontSize(11);
            col.Item().PaddingTop(10);

            // Top criteria failures — up to 5 failed sub-results
            var topFailures = subResults
                .Where(s => !s.Score.Passed)
                .Take(5)
                .ToList();

            if (topFailures.Count > 0)
            {
                col.Item().Text("Top criteria failures").FontSize(14).Bold();
                foreach (var failure in topFailures)
                {
                    col.Item().PaddingTop(10).Text(failure.Metric.Key).FontSize(12).Bold();

                    var evidenceMsg = failure.Details.Evidence?.FirstOrDefault()?.Message;
                    if (!string.IsNullOrEmpty(evidenceMsg))
                        col.Item().Text($"Evidence: {TruncateForPreview(evidenceMsg, 200)}").FontSize(10);

                    var reference = failure.Details.Evidence?.FirstOrDefault()?.Reference;
                    if (!string.IsNullOrEmpty(reference))
                        col.Item().Text($"Reference: {TruncateForPreview(reference, 300)}").FontSize(10).Italic();
                }
            }
            else if (subResults.Count > 0)
            {
                col.Item().Text("All sub-results passed or no failures to display.").FontSize(11).Italic();
            }
        });
    }

    // ── Appendix A — Audit chain ─────────────────────────────────────────────

    private static void RenderAuditChainAppendix(PageDescriptor page, AgenticBenchmarkResult result)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Appendix A: Audit Chain").FontSize(18).Bold();
            col.Item().PaddingTop(15);

            col.Item().Text($"Source run ID: {result.SourceRunId}");
            col.Item().Text($"Manifest hash: {result.SourceManifestHash}");
            col.Item().Text($"Schema version: 1.0");
            col.Item().PaddingTop(10);

            col.Item().Text($"AgentEval version: {result.Attestation.AgentEvalVersion}");
            col.Item().Text($"Judge mode: {result.Attestation.JudgeMode}");
            col.Item().PaddingTop(10);

            col.Item().Text("Prompt versions:").Bold();
            foreach (var kv in result.Attestation.PromptVersions)
                col.Item().PaddingLeft(20).Text($"{kv.Key}: {kv.Value}");
        });
    }

    // ── Appendix B — Methodology ─────────────────────────────────────────────

    private static void RenderMethodologyAppendix(PageDescriptor page, AgenticBenchmarkResult result)
    {
        page.Margin(40);
        page.Content().Column(col =>
        {
            col.Item().Text("Appendix B: Methodology").FontSize(18).Bold();
            col.Item().PaddingTop(15);

            col.Item().Text($"Preset: {FormatPresetTitle(result.Preset)} Preset").Bold();
            col.Item().PaddingTop(8).Text(GetPresetDescription(result.Preset)).FontSize(11);

            col.Item().PaddingTop(15).Text("Judge Mode").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(GetJudgeModeDescription(result.Attestation.JudgeMode)).FontSize(11);

            col.Item().PaddingTop(15).Text("Evaluation Categories").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "The agentic benchmark evaluates agent behavior across up to seven categories:\n" +
                "  system-outcome — Did the agent achieve the intended task goal?\n" +
                "  agentic-process — Were agentic reasoning steps, tool calls, and planning appropriate?\n" +
                "  rag — Retrieval-Augmented Generation quality: relevance, grounding, and citation accuracy.\n" +
                "  safety-security — Did the agent avoid unsafe, harmful, or policy-violating outputs?\n" +
                "  operational — Latency, cost, retry, and reliability characteristics.\n" +
                "  judge-quality — Meta-evaluation of the judge's own scoring consistency.\n" +
                "  agentic — Top-level composite rollup (present in flat presets).\n\n" +
                "Overall score is the weighted composite across all active categories and evaluators. " +
                "Pass/warn/fail thresholds are preset-specific (see preset description above).").FontSize(11);

            col.Item().PaddingTop(15).Text("Severity Tiers").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "critical — the failure represents a significant, potentially harmful deviation " +
                "from expected agent behavior (e.g. safety violations, complete task failure).\n" +
                "high — important gap requiring remediation before production deployment.\n" +
                "medium — notable gap; acceptable for early-stage evaluation but should be addressed.\n" +
                "low — minor issue; recommend improvement but does not block deployment.\n" +
                "none — informational; no behavioral impact.").FontSize(11);

            col.Item().PaddingTop(15).Text("Prompt Provenance").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(
                "Evaluator prompts are forked from public MIT-licensed sources (Azure SDK for Python " +
                "azure-ai-evaluation evaluator prompty files) and improved per the AgentEval " +
                "envelope (temperature=0, structured evidence[], severity rubric, sub-dimensions, " +
                "deterministic-first paths where applicable). Each prompt file's header carries " +
                "source URL + commit SHA + the list of modifications applied.").FontSize(11);

            col.Item().PaddingTop(15).Text("Prompt Versions").FontSize(14).Bold();
            col.Item().PaddingTop(5);
            foreach (var kv in result.Attestation.PromptVersions)
                col.Item().Text($"  {kv.Key}: {kv.Value}").FontSize(11);

            col.Item().PaddingTop(15).Text("Disclaimer").FontSize(14).Bold();
            col.Item().PaddingTop(5).Text(result.Disclaimer).FontSize(10).Italic();
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string StatusColor(string status) => status switch
    {
        "PASS" => Colors.Green.Medium,
        "WARN" => Colors.Orange.Medium,
        _ => Colors.Red.Medium
    };

    private static string FormatPresetTitle(string preset)
    {
        if (string.IsNullOrEmpty(preset)) return preset;
        // Convert kebab-case to Title Case: "agentic-execution" → "Agentic-Execution"
        return string.Join("-", preset.Split('-')
            .Select(part => part.Length > 0
                ? char.ToUpperInvariant(part[0]) + part[1..]
                : part));
    }

    private static string GetJudgeModeDescription(string judgeMode) => judgeMode.ToLowerInvariant() switch
    {
        "stub" or "mode-a" =>
            "Stub judge mode: evaluators return deterministic fixed responses based on scenario metadata. " +
            "Designed for fast CI validation without requiring a live LLM endpoint.",
        "single" or "mode-b" or "real" or "llm" =>
            "Single LLM judge mode: each scenario is evaluated by a single live language model call " +
            "configured via the benchmark's judge pipeline. Results reflect genuine model behavior " +
            "against each scenario's evaluation criteria.",
        "panel" =>
            "Panel judge mode: multiple independent LLM calls are made per scenario and their verdicts " +
            "are aggregated. Provides higher reliability at increased cost.",
        "adjudicated" =>
            "Adjudicated judge mode: a panel of judges is convened and disagreements are resolved by a " +
            "dedicated adjudicator model. Provides the highest fidelity evaluation.",
        _ =>
            $"Judge mode: {judgeMode}. Refer to benchmark configuration for details on the evaluation pipeline."
    };

    private static string GetPresetDescription(string preset) => preset.ToLowerInvariant() switch
    {
        "agentic-execution" =>
            "The Agentic-Execution preset is the standard agentic benchmark, covering the six core " +
            "evaluation dimensions: system-outcome, agentic-process, RAG quality, safety-security, " +
            "operational, and judge-quality. It is the recommended preset for development-phase " +
            "behavioral regression testing. Pass threshold is 0.80 (80%).",
        "tool-call-accuracy" =>
            "The Tool-Call-Accuracy preset focuses on the precision and recall of tool invocations: " +
            "whether the agent calls the correct tools with correctly-formed parameters, handles " +
            "errors gracefully, and avoids spurious or missing tool calls. Pass threshold is 0.85.",
        "safety" =>
            "The Safety preset is focused exclusively on safety-security category evaluators. " +
            "It provides a fast, targeted safety gate for CI pipelines. Pass threshold is 0.90.",
        "rag-quality" =>
            "The RAG-Quality preset targets retrieval-augmented generation evaluators: context " +
            "relevance, groundedness, answer relevance, and citation accuracy. Pass threshold is 0.85.",
        "judge-quality" =>
            "The Judge-Quality preset is a meta-evaluation preset that assesses the internal " +
            "consistency and calibration of the LLM judge itself. Pass threshold is 0.85.",
        _ =>
            $"The {FormatPresetTitle(preset)} preset covers the configured evaluator set for agentic " +
            "benchmark assessment. Refer to the benchmark configuration for preset-specific thresholds " +
            "and aggregation rules."
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

    /// <summary>
    /// Returns per-category entries in canonical order: known categories first
    /// (in <see cref="s_categoryOrder"/> order), then any additional categories alphabetically.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, AgenticCategorySummary>> OrderedCategories(
        IReadOnlyDictionary<string, AgenticCategorySummary> perCategory)
    {
        var known = s_categoryOrder
            .Where(perCategory.ContainsKey)
            .Select(k => new KeyValuePair<string, AgenticCategorySummary>(k, perCategory[k]));

        var extra = perCategory
            .Where(kv => !s_categoryOrder.Contains(kv.Key, StringComparer.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal);

        return known.Concat(extra);
    }

    /// <summary>
    /// Attempts to determine the category of an evaluator by walking the composite tree.
    /// Falls back to examining the evaluator key for known category keywords.
    /// </summary>
    private static string GetEvaluatorCategory(string evaluatorKey, EvalResult compositeTree)
    {
        // Walk the tree: if we find the evaluator as a direct child of a category node,
        // return that category node's category label.
        var result = FindResultByKey(compositeTree, evaluatorKey);
        if (result?.Metric.Category is { Length: > 0 } cat)
            return cat;

        // Fallback: infer from key naming conventions
        return evaluatorKey.ToLowerInvariant() switch
        {
            var k when k.Contains("task") || k.Contains("outcome") || k.Contains("completion") =>
                "system-outcome",
            var k when k.Contains("tool") || k.Contains("planning") || k.Contains("reasoning") ||
                       k.Contains("step") || k.Contains("action") =>
                "agentic-process",
            var k when k.Contains("rag") || k.Contains("retrieval") || k.Contains("grounded") ||
                       k.Contains("citation") || k.Contains("relevance") =>
                "rag",
            var k when k.Contains("safety") || k.Contains("harm") || k.Contains("jailbreak") ||
                       k.Contains("prompt-injection") || k.Contains("pii") =>
                "safety-security",
            var k when k.Contains("latency") || k.Contains("cost") || k.Contains("operational") ||
                       k.Contains("retry") || k.Contains("reliability") =>
                "operational",
            var k when k.Contains("judge") || k.Contains("calibration") || k.Contains("consistency") =>
                "judge-quality",
            _ => "agentic"
        };
    }

    private static string TruncateForPreview(string? text, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= maxLen ? text : text[..maxLen] + "…";
    }
}
