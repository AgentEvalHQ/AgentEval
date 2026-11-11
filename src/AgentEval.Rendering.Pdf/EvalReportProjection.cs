// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Globalization;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Reporting;

namespace AgentEval.Rendering.Pdf;

/// <summary>Projects recorded evaluation facts into an engine-neutral PDF report, applying privacy before provider access.</summary>
public static class EvalReportProjection
{
    /// <summary>Creates a report from the authoritative result without changing its score or verdict.</summary>
    public static ReportDocument Create(EvalResult result, EvalResultRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        var generatedAt = options.GeneratedAt ?? DateTimeOffset.UtcNow;
        var cover = new List<string>
        {
            options.RegulationOrBenchmark ?? "AgentEval Report",
            $"Subject: {options.Subject.Name} ({options.Subject.Kind})",
            $"Generated: {generatedAt:O}",
            $"OVERALL: {Label(result.Score)} ({Score(result.Score, "P0")})",
        };
        Add(cover, "Model", options.Subject.ModelId);
        Add(cover, "Framework", options.Subject.Framework);
        Add(cover, "Run ID", options.RunId);
        if (!string.IsNullOrWhiteSpace(result.Details.Summary)) cover.Add(result.Details.Summary);
        cover.AddRange(Recommendations(result));
        var sections = new List<ReportSection> { new(options.Title ?? result.Metric.Name, cover) };
        if (result.Details.SubResults is { Count: > 0 } children)
            sections.Add(new("Component Summary", tables: [new(["Component", "Score", "Label", "Severity"],
                children.Select(child => new[] { child.Metric.Key, Score(child.Score, "P0"), Label(child.Score), child.Score.Severity ?? "none" }))]));
        AppendDetails(sections, result, options, 0, isRoot: true);
        var audit = new List<string> { $"Generated: {generatedAt:O}", $"Root key: {result.Metric.Key}" };
        Add(audit, "Audit hash", options.AuditHash);
        Add(audit, "Run ID", options.RunId);
        Add(audit, "AgentEval version", options.AgentEvalVersion);
        if (options.IncludeProvenance)
        {
            Add(audit, "Root evaluator", result.Provenance.Type);
            Add(audit, "Judge model", result.Provenance.JudgeModel);
        }
        audit.Add("This report presents recorded evaluations and does not constitute legal advice or certification.");
        sections.Add(new("Appendix: Audit Trail", audit));
        return new(options.Title ?? result.Metric.Name, sections, generatedAt, options.Subject.Name, "AgentEval");
    }

    private static void AppendDetails(List<ReportSection> sections, EvalResult node, EvalResultRenderOptions options, int depth, bool isRoot)
    {
        if (depth > EvalTreeLimits.MaxTreeWalkDepth)
            throw new ArgumentException("Expected: a bounded evaluation tree. Actual: maximum report depth exceeded. Suggestions: remove cycles or reduce nesting. Because: a report must not silently drop results.", nameof(node));
        var paragraphs = new List<string>
        {
            $"Key: {node.Metric.Key} | Category: {node.Metric.Category} | v{node.Metric.Version}",
            $"Score: {Score(node.Score, "P1")} | Label: {Label(node.Score)} | Severity: {node.Score.Severity}",
        };
        if (!isRoot)
        {
            if (!string.IsNullOrWhiteSpace(node.Details.Summary)) paragraphs.Add(node.Details.Summary);
            paragraphs.AddRange(Recommendations(node));
        }
        if (options.IncludeProvenance)
        {
            paragraphs.Add("Provenance");
            Add(paragraphs, "Evaluator", node.Provenance.Type);
            Add(paragraphs, "Judge", node.Provenance.JudgeModel);
            Add(paragraphs, "Prompt ID", node.Provenance.PromptId);
            if (node.Provenance.TokensUsed is { } tokens) paragraphs.Add($"Tokens: {tokens}");
            paragraphs.Add(node.Provenance.EstimatedCost is { } cost ? $"Est. cost (USD): {cost:F4}" : "Est. cost (USD): not priced");
            paragraphs.Add($"Cache hit: {(node.Provenance.CacheHit ? "yes" : "no")}");
        }
        foreach (var evidence in node.Details.Evidence ?? [])
            paragraphs.Add($"[{evidence.Source}] {TruncateEvidence(evidence.Message ?? "", options.EvidenceTruncationLength)}" +
                (string.IsNullOrEmpty(evidence.Reference) ? "" : $" ({evidence.Reference})"));
        var tables = node.Details.Dimensions is { Count: > 0 } dimensions
            ? new[] { new ReportTable(["Metric", "Value"], dimensions.Select(pair => new[] { pair.Key, pair.Value.ToString("G6", CultureInfo.InvariantCulture) })) }
            : [];
        sections.Add(new(node.Metric.Name, paragraphs, tables));
        foreach (var child in node.Details.SubResults ?? []) AppendDetails(sections, child, options, depth + 1, false);
    }

    private static IEnumerable<string> Recommendations(EvalResult node)
    {
        var summary = node.Details.Summary ?? "";
        return (node.Details.Recommendations ?? []).Where(r => !string.IsNullOrWhiteSpace(r)
            && !string.Equals(r.Trim(), summary.Trim(), StringComparison.Ordinal)
            && (r.Length < 20 || !summary.Contains(r, StringComparison.Ordinal))).Select(r => "• " + r);
    }

    /// <summary>Applies a presentation limit without splitting surrogate pairs, and discloses omitted content.</summary>
    public static string TruncateEvidence(string text, int maxLength)
    {
        if (maxLength <= 0 || text.Length <= maxLength) return text;
        if (char.IsHighSurrogate(text[maxLength - 1])) maxLength--;
        var tail = text[maxLength..];
        var lines = tail.Count(c => c == '\n');
        return text[..maxLength] + (lines > 0
            ? $" … (truncated; {lines} more lines, {tail.Length:N0} more chars)"
            : $" … (truncated; {tail.Length:N0} more chars)");
    }

    /// <summary>Describes a recorded source hash without claiming verification this projection did not perform.</summary>
    public static IReadOnlyList<string> AuditChainLines(SourceRunRef sourceRun)
    {
        ArgumentNullException.ThrowIfNull(sourceRun);
        return string.IsNullOrWhiteSpace(sourceRun.ManifestHash)
            ? ["Manifest hash: —", "Chain status: no hash recorded, so this evidence cannot be checked against its source run."]
            : [$"Manifest hash: {sourceRun.ManifestHash}", "Chain status: hash recorded, not verified in this report.",
                "To verify, run 'agenteval doctor' inside the solution whose .agenteval/ workspace holds this source run. It re-hashes each run's files against that run's manifest.json and checks the manifest hash in every compliance evidence.json against its source run. Then confirm the hash above equals contentHash in that run's manifest.json."];
    }

    private static string Score(EvalScore score, string format) => score.MeasuredValue() is { } value ? value.ToString(format, CultureInfo.InvariantCulture) : "not measured";
    private static string Label(EvalScore score) => string.Equals(score.Label, "skipped", StringComparison.OrdinalIgnoreCase) ? "NOT TESTED" : score.Label.ToUpperInvariant();
    private static void Add(List<string> lines, string label, string? value) { if (!string.IsNullOrEmpty(value)) lines.Add($"{label}: {value}"); }
}
