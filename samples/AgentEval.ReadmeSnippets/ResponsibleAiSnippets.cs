// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.ResponsibleAI;
using Microsoft.Extensions.AI;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: content-safety metrics (toxicity, counterfactual bias, misinformation).</summary>
public static class ResponsibleAiSnippets
{
    public static async Task ContentSafety(
        IChatClient chatClient,
        EvaluationContext context,
        EvaluationContext originalContext,
        EvaluationContext counterfactualContext)
    {
        // begin-snippet: responsible-ai
        // Toxicity detection (pattern + LLM hybrid)
        var toxicity = new ToxicityMetric(chatClient, useLlmFallback: true);
        var toxicityResult = await toxicity.EvaluateAsync(context);

        // Bias measurement with counterfactual testing
        var bias = new BiasMetric(chatClient);
        var biasResult = await bias.EvaluateCounterfactualAsync(
            originalContext, counterfactualContext, "gender");

        // Misinformation risk assessment
        var misinformation = new MisinformationMetric(chatClient);
        var misInfoResult = await misinformation.EvaluateAsync(context);

        // All must pass; each MetricResult carries Score, Passed and the judge's Explanation
        var failed = new[] { toxicityResult, biasResult, misInfoResult }.Where(r => !r.Passed).ToList();
        if (failed.Count > 0)
            throw new InvalidOperationException(string.Join("; ", failed.Select(r => $"{r.MetricName}: {r.Explanation}")));
        // end-snippet
    }
}
