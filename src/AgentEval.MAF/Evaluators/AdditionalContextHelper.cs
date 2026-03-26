// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts typed data from MEAI's <c>additionalContext</c> parameter.
/// </summary>
public static class AdditionalContextHelper
{
    /// <summary>
    /// Extracts the RAG retrieval context from additional context, if provided.
    /// </summary>
    public static string? ExtractRAGContext(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalRAGContext ragCtx)
                return ragCtx.RetrievedContext;
        }
        return null;
    }

    /// <summary>
    /// Extracts the ground truth from additional context, if provided.
    /// </summary>
    public static string? ExtractGroundTruth(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalGroundTruthContext gtCtx)
                return gtCtx.GroundTruth;
        }
        return null;
    }

    /// <summary>
    /// Extracts expected tool names from additional context, if provided.
    /// </summary>
    public static IReadOnlyList<string>? ExtractExpectedTools(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalExpectedToolsContext toolsCtx)
                return toolsCtx.ExpectedToolNames;
        }
        return null;
    }
}

/// <summary>
/// Carries RAG retrieval context through MEAI's <c>additionalContext</c> parameter.
/// </summary>
public class AgentEvalRAGContext : MEAIEvaluationContext
{
    /// <summary>Creates a RAG context with the retrieved documents.</summary>
    public AgentEvalRAGContext(string retrievedContext)
        : base("AgentEvalRAGContext", retrievedContext)
    {
        RetrievedContext = retrievedContext;
    }

    /// <summary>The retrieved context/documents for RAG evaluation.</summary>
    public string RetrievedContext { get; }
}

/// <summary>
/// Carries ground truth expected output through MEAI's <c>additionalContext</c> parameter.
/// </summary>
public class AgentEvalGroundTruthContext : MEAIEvaluationContext
{
    /// <summary>Creates a ground truth context.</summary>
    public AgentEvalGroundTruthContext(string groundTruth)
        : base("AgentEvalGroundTruth", groundTruth)
    {
        GroundTruth = groundTruth;
    }

    /// <summary>The expected ground truth answer.</summary>
    public string GroundTruth { get; }
}

/// <summary>
/// Carries expected tool call names through MEAI's <c>additionalContext</c> parameter.
/// </summary>
public class AgentEvalExpectedToolsContext : MEAIEvaluationContext
{
    /// <summary>Creates an expected tools context.</summary>
    public AgentEvalExpectedToolsContext(IReadOnlyList<string> expectedToolNames)
        : base("AgentEvalExpectedTools", string.Join(", ", expectedToolNames))
    {
        ExpectedToolNames = expectedToolNames;
    }

    /// <summary>Names of tools expected to be called.</summary>
    public IReadOnlyList<string> ExpectedToolNames { get; }
}
