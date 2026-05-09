// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// Walks the agentic benchmark composite result tree depth-first and returns all leaf
/// <see cref="EvalResult"/> nodes where <c>Score.Severity == "critical"</c>
/// and <c>!Score.Passed</c>, ordered by <c>Score.Value</c> ascending (worst first).
/// </summary>
public sealed class AgenticCriticalFindingExtractor
{
    /// <summary>
    /// Finds all critical failing leaf nodes in the result tree rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>
    /// A list of critical-severity failing leaf results, ordered by score ascending (worst first).
    /// </returns>
    public IReadOnlyList<EvalResult> Find(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var findings = new List<EvalResult>();
        WalkAtomicLeaves(root, findings);
        return findings
            .Where(f => f.Score.Severity == "critical" && !f.Score.Passed)
            .OrderBy(f => f.Score.Value)
            .ToList();
    }

    private static void WalkAtomicLeaves(EvalResult node, List<EvalResult> sink)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0) { sink.Add(node); return; }
        foreach (var s in subs) WalkAtomicLeaves(s, sink);
    }
}

/// <summary>
/// Walks the agentic benchmark composite result tree, collects failed evaluator keys, and maps
/// each to a static short remediation string. Dynamic LLM-generated recommendations are deferred
/// to a future phase.
/// </summary>
public static class AgenticRecommendationExtractor
{
    private static readonly IReadOnlyDictionary<string, string> s_recommendations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["task_completion"]       = "Review task completion failures: ensure the agent fully addresses the user's stated goal before terminating.",
            ["task_adherence"]        = "Review task adherence failures: ensure the agent follows its system prompt constraints and does not deviate from the intended scope.",
            ["intent_recognition"]    = "Improve intent recognition: add training examples or retrieval context for the failing intent classes.",
            ["intent_disambiguation"] = "Add explicit clarification turns for ambiguous intents rather than guessing.",
            ["tool_call_accuracy"]    = "Audit tool-call failures: verify the agent selects the correct tool, passes valid arguments, and handles tool errors gracefully.",
            ["tool_call_frequency"]   = "Check tool invocation rate: ensure the agent calls tools when required and avoids redundant or missing calls.",
            ["tool_selection"]        = "Improve tool selection: verify the agent's tool routing logic aligns with the available tool set.",
            ["safety_refusal"]        = "Strengthen safety refusal behaviour: ensure the agent declines harmful requests consistently across paraphrase variants.",
            ["safety_boundary"]       = "Harden safety boundaries: probe edge cases and update the system prompt or fine-tuning data to cover identified gaps.",
            ["rag_faithfulness"]      = "Reduce RAG hallucinations: constrain the agent to cite only retrieved context and avoid unsupported claims.",
            ["rag_relevance"]         = "Improve RAG relevance: tune the retrieval pipeline (chunk size, embedding model, reranker) to surface more pertinent passages.",
            ["rag_groundedness"]      = "Increase groundedness: require the agent to attribute every factual claim to a retrieved source.",
            ["judge_calibration"]     = "Review judge calibration: compare LLM judge scores against human labels and adjust judge prompts or thresholds.",
        };

    /// <summary>
    /// Builds a list of recommendation strings for every failed evaluator in the result tree.
    /// </summary>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>
    /// Recommendations in evaluator-key alphabetical order. Each item is the static recommendation
    /// for that evaluator key, or a generic fallback when no mapping is registered.
    /// </returns>
    public static IReadOnlyList<string> Build(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkEvaluators(root, failed);
        return failed
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => s_recommendations.TryGetValue(k, out var rec) ? rec : $"Review failures in {k}.")
            .ToList();
    }

    private static void WalkEvaluators(EvalResult node, HashSet<string> sink)
    {
        if (!node.Score.Passed && !string.IsNullOrEmpty(node.Metric.Key))
            sink.Add(node.Metric.Key);

        var subs = node.Details.SubResults;
        if (subs is null) return;
        foreach (var s in subs) WalkEvaluators(s, sink);
    }
}
