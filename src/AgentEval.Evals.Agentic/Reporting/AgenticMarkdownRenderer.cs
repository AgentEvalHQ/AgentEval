// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// Renders an <see cref="AgenticBenchmarkResult"/> as a PR-friendly Markdown report.
/// The report opens with the required disclaimer, followed by overall status, per-category
/// and per-evaluator tables, critical findings, and recommendations.
/// </summary>
public sealed class AgenticMarkdownRenderer
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
    /// Renders <paramref name="result"/> to a Markdown string.
    /// </summary>
    /// <param name="result">The agentic benchmark result to render.</param>
    /// <returns>A Markdown document as a string.</returns>
    public string Render(AgenticBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();

        var presetTitle = FormatPresetTitle(result.Preset);

        sb.AppendLine($"# Agentic Benchmark — {presetTitle} Preset");
        sb.AppendLine();
        sb.AppendLine($"> {result.Disclaimer}");
        sb.AppendLine();

        sb.AppendLine($"**Subject**: `{result.Subject.Name}` ({result.Subject.Kind})");
        sb.AppendLine($"**Run**: `{result.SourceRunId}`");
        sb.AppendLine($"**Generated**: {result.GeneratedAt:O}");
        sb.AppendLine($"**Overall**: **{result.Summary.OverallStatus}** (score {result.Summary.OverallScore:P0})");
        sb.AppendLine();

        // Per-category table — suppress when empty (e.g. very flat presets)
        if (result.Summary.PerCategory.Count > 0)
        {
            sb.AppendLine("## Per-category");
            sb.AppendLine();
            sb.AppendLine("| Category | Score | Status | Critical fails |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var (key, cat) in OrderedCategories(result.Summary.PerCategory))
            {
                var criticals = cat.CriticalFails.Count == 0
                    ? "—"
                    : string.Join(", ", cat.CriticalFails.Select(k => $"`{k}`"));
                sb.AppendLine($"| {key} | {cat.Score:P0} | **{cat.Status}** | {criticals} |");
            }
            sb.AppendLine();
        }

        // Per-evaluator table
        if (result.Summary.PerEvaluator.Count > 0)
        {
            sb.AppendLine("## Per-evaluator");
            sb.AppendLine();
            sb.AppendLine("| Evaluator | Score | Status | Severity | Confidence |");
            sb.AppendLine("|---|---:|---|---|---:|");
            foreach (var (key, ev) in result.Summary.PerEvaluator.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var conf = ev.Confidence.HasValue ? $"{ev.Confidence.Value:P0}" : "—";
                sb.AppendLine($"| `{key}` | {ev.Score:P0} | **{ev.Status}** | {ev.Severity} | {conf} |");
            }
            sb.AppendLine();
        }

        // Critical findings
        if (result.CriticalFindings.Count > 0)
        {
            sb.AppendLine("## Critical findings");
            sb.AppendLine();
            foreach (var f in result.CriticalFindings)
                sb.AppendLine($"- **{f.Metric.Key}** — score {f.Score.Value:P0}, severity `{f.Score.Severity}`");
            sb.AppendLine();
        }

        // Recommendations
        if (result.Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            sb.AppendLine();
            foreach (var r in result.Recommendations)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string FormatPresetTitle(string preset)
    {
        if (string.IsNullOrEmpty(preset)) return preset;
        // Convert kebab-case to Title Case: "agentic-execution" → "Agentic-Execution"
        return string.Join("-", preset.Split('-')
            .Select(part => part.Length > 0
                ? char.ToUpperInvariant(part[0]) + part[1..]
                : part));
    }

    /// <summary>
    /// Returns the per-category entries in canonical order: known categories first (in
    /// <see cref="s_categoryOrder"/> order), then any additional categories alphabetically.
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
}
