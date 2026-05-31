// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// The single category-inference heuristic for agentic evaluators (ARC-11). Previously
/// <see cref="AgenticSummaryBuilder"/> (StartsWith) and the PDF renderer (Contains, over a DIFFERENT
/// keyword set and a different default) each carried their own fallback, so the two reports could in
/// principle bucket an evaluator differently. Both now share this one keyword map. It is only consulted
/// when an evaluator's canonical <c>Metric.Category</c> is absent (the common case sets it), so this is
/// the last-resort path — but it is now consistent across reports.
/// </summary>
internal static class AgenticCategoryResolver
{
    /// <summary>
    /// Infers a category from an evaluator key by case-insensitive keyword match. Returns the canonical
    /// category id, defaulting to <c>"operational"</c> when nothing matches.
    /// </summary>
    public static string InferCategoryFromKey(string? evaluatorKey)
    {
        var k = (evaluatorKey ?? string.Empty).ToLowerInvariant();
        return k switch
        {
            _ when k.Contains("task") || k.Contains("intent") || k.Contains("outcome") || k.Contains("completion")
                => "system-outcome",
            _ when k.Contains("tool") || k.Contains("planning") || k.Contains("reasoning") || k.Contains("step") || k.Contains("action")
                => "agentic-process",
            _ when k.Contains("rag") || k.Contains("retrieval") || k.Contains("grounded") || k.Contains("citation") || k.Contains("relevance")
                => "rag",
            _ when k.Contains("safety") || k.Contains("refusal") || k.Contains("harm") || k.Contains("jailbreak") || k.Contains("prompt-injection") || k.Contains("pii")
                => "safety-security",
            _ when k.Contains("judge") || k.Contains("calibration") || k.Contains("consistency")
                => "judge-quality",
            _ when k.Contains("latency") || k.Contains("cost") || k.Contains("operational") || k.Contains("retry") || k.Contains("reliability")
                => "operational",
            _ => "operational"
        };
    }
}
