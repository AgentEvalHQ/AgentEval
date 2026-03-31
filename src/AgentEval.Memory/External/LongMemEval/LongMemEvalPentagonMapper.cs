// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Maps LongMemEval results to the paper's 5 ability dimensions:
///   1. Information Extraction (IE) = avg(SSU, SSA, SSP)
///   2. Multi-Session Reasoning (MR) = multi-session
///   3. Temporal Reasoning (TR)      = temporal-reasoning
///   4. Knowledge Updates (KU)       = knowledge-update
///   5. Abstention (ABS)             = cross-type, identified by _abs suffix on question_id
///
/// These are NOT the native AgentEval pentagon dimensions.
/// See: LongMemEval (ICLR 2025), Table 1 — "Five core memory abilities"
/// </summary>
public static class LongMemEvalPentagonMapper
{
    /// <summary>
    /// Consolidates per-type results + question-level results into the paper's 5 dimensions.
    /// The <paramref name="perTypeResults"/> provides the 6-type breakdown.
    /// The <paramref name="questionResults"/> is needed to extract cross-type abstention scores.
    /// </summary>
    public static Dictionary<string, double> Consolidate(
        Dictionary<string, TypeResult> perTypeResults,
        IReadOnlyList<QuestionResult>? questionResults = null)
    {
        ArgumentNullException.ThrowIfNull(perTypeResults);

        var scores = new Dictionary<string, double>();

        // 1. Information Extraction = avg(SSU, SSA, SSP)
        //    Can the agent extract a specific fact from ~115K tokens of conversation history?
        AddAveraged(scores, "Information Extraction", perTypeResults,
            "single-session-user", "single-session-assistant", "single-session-preference");

        // 2. Multi-Session Reasoning = multi-session (non-abs only if we have question-level data)
        //    Can the agent synthesize partial facts scattered across 2-6 different sessions?
        if (perTypeResults.TryGetValue("multi-session", out var ms))
            scores["Multi-Session"] = ms.Accuracy;

        // 3. Temporal Reasoning = temporal-reasoning
        //    Can the agent reason about time ordering, durations, and event sequences?
        if (perTypeResults.TryGetValue("temporal-reasoning", out var tr))
            scores["Temporal"] = tr.Accuracy;

        // 4. Knowledge Updates = knowledge-update
        //    Can the agent track corrected/updated facts and return the latest value?
        if (perTypeResults.TryGetValue("knowledge-update", out var ku))
            scores["Knowledge Update"] = ku.Accuracy;

        // 5. Abstention = cross-type, all questions with _abs suffix on question_id
        //    Does the agent refuse to fabricate answers about non-existent information?
        if (questionResults != null)
        {
            var absQuestions = questionResults.Where(q => q.QuestionId.EndsWith("_abs", StringComparison.Ordinal)).ToList();
            if (absQuestions.Count > 0)
            {
                var absCorrect = absQuestions.Count(q => q.Correct);
                scores["Abstention"] = (double)absCorrect / absQuestions.Count * 100;
            }
        }

        return scores;
    }

    /// <summary>
    /// Legacy overload for backward compatibility with ToBaseline delegate signature.
    /// Falls back to per-type only (no abstention extraction from question-level data).
    /// </summary>
    public static Dictionary<string, double> Consolidate(
        Dictionary<string, TypeResult> perTypeResults)
        => Consolidate(perTypeResults, questionResults: null);

    private static void AddAveraged(
        Dictionary<string, double> scores,
        string dimension,
        Dictionary<string, TypeResult> perTypeResults,
        params string[] types)
    {
        var values = types
            .Where(t => perTypeResults.ContainsKey(t))
            .Select(t => perTypeResults[t].Accuracy)
            .ToList();

        if (values.Count > 0)
            scores[dimension] = values.Average();
    }
}
