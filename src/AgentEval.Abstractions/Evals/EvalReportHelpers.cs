// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// QuestPDF-independent helpers shared by the compliance / agentic report renderers (ARC-02). The GDPR,
/// EU AI Act and Agentic PDF renderers each reimplemented an identical recursive find-by-key, preview
/// truncation, and capitalisation; they now share these so the logic cannot drift. (QuestPDF-typed helpers
/// such as StatusColor stay in the renderers since this assembly does not reference QuestPDF.)
/// </summary>
public static class EvalReportHelpers
{
    /// <summary>
    /// Depth-first search of an <see cref="EvalResult"/> composite tree for the leaf/node whose
    /// <c>Metric.Key</c> equals <paramref name="key"/>; null if absent. Bounded by
    /// <see cref="EvalTreeLimits.MaxTreeWalkDepth"/> (ARC-03) so a pathological tree cannot stack-overflow
    /// the renderer.
    /// </summary>
    public static EvalResult? FindByKey(EvalResult root, string key, int depth = 0)
    {
        if (depth > EvalTreeLimits.MaxTreeWalkDepth) return null;
        if (root.Metric.Key == key) return root;
        if (root.Details.SubResults is null) return null;
        foreach (var child in root.Details.SubResults)
        {
            var found = FindByKey(child, key, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Collapses newlines and truncates <paramref name="text"/> to <paramref name="maxLen"/> characters
    /// (appending an ellipsis) for a single-line table/preview cell. Empty string for null/empty input.
    /// </summary>
    public static string TruncateForPreview(string? text, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= maxLen ? text : text[..maxLen] + "…";
    }

    /// <summary>Upper-cases the first character of <paramref name="s"/>.</summary>
    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
