// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using Microsoft.Extensions.AI;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: RAG quality metrics graded by an LLM judge.</summary>
public static class RagSnippets
{
    public static async Task RagQuality(IChatClient judgeClient, string agentResponse, string retrievedDocuments)
    {
        // begin-snippet: rag-quality
        var context = new EvaluationContext
        {
            Input = "What are the return policy terms?",
            Output = agentResponse,
            Context = retrievedDocuments,
            GroundTruth = "30-day return policy with receipt"
        };

        // judgeClient is the IChatClient that grades the answer
        var faithfulness = await new FaithfulnessMetric(judgeClient).EvaluateAsync(context);
        var relevance = await new RelevanceMetric(judgeClient).EvaluateAsync(context);
        var correctness = await new AnswerCorrectnessMetric(judgeClient).EvaluateAsync(context);

        // Detect hallucinations. Passed is also false when the judge's reply could not be parsed,
        // so read Explanation before blaming the agent.
        if (!faithfulness.Passed)
            throw new InvalidOperationException($"Faithfulness {faithfulness.Score:F0}: {faithfulness.Explanation}");
        // end-snippet
    }
}
