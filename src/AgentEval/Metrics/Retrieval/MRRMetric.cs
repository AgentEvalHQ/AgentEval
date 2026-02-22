// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Metrics.Retrieval;

/// <summary>
/// Measures Mean Reciprocal Rank (MRR) - how early the first relevant document appears.
/// 
/// Formula: MRR = 1 / rank_of_first_relevant (or 0 if none found)
/// 
/// This is a code-based metric (free - no LLM calls).
/// </summary>
/// <remarks>
/// <para>
/// MRR answers: "How quickly did we find a relevant document?"
/// </para>
/// <para>
/// - MRR = 1.0 means the first result was relevant
/// - MRR = 0.5 means the first relevant result was at position 2
/// - MRR = 0.33 means the first relevant result was at position 3
/// - MRR = 0.0 means no relevant result was found
/// </para>
/// <para>
/// Use this metric to evaluate your retrieval system's ranking quality.
/// </para>
/// <para>
/// Required in EvaluationContext.Properties:
/// <list type="bullet">
///   <item>"RetrievedDocumentIds" - IReadOnlyList&lt;string&gt; of document IDs (ordered by rank)</item>
///   <item>"RelevantDocumentIds" - IReadOnlyList&lt;string&gt; of ground truth relevant document IDs</item>
/// </list>
/// </para>
/// </remarks>
public class MRRMetric : IRAGMetric
{
    /// <summary>
    /// Property key for retrieved document IDs in EvaluationContext.Properties.
    /// </summary>
    public const string RetrievedDocumentIdsKey = "RetrievedDocumentIds";
    
    /// <summary>
    /// Property key for relevant document IDs in EvaluationContext.Properties.
    /// </summary>
    public const string RelevantDocumentIdsKey = "RelevantDocumentIds";
    
    private readonly int? _maxRank;
    
    /// <summary>
    /// Initializes a new instance of the MRRMetric.
    /// </summary>
    /// <param name="maxRank">Optional: Maximum rank to consider (null = no limit).</param>
    public MRRMetric(int? maxRank = null)
    {
        if (maxRank.HasValue && maxRank.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRank), "maxRank must be positive");
        _maxRank = maxRank;
    }

    /// <inheritdoc />
    public string Name => _maxRank.HasValue ? $"code_mrr_at_{_maxRank}" : "code_mrr";

    /// <inheritdoc />
    public string Description => _maxRank.HasValue 
        ? $"Reciprocal rank of first relevant document (considering top {_maxRank})" 
        : "Reciprocal rank of first relevant document";

    /// <inheritdoc />
    public MetricCategory Categories => 
        MetricCategory.RAG | 
        MetricCategory.CodeBased | 
        MetricCategory.RequiresGroundTruth;

    /// <inheritdoc />
    public decimal? EstimatedCostPerEvaluation => 0m; // Code-based = FREE

    /// <inheritdoc />
    public bool RequiresContext => false;

    /// <inheritdoc />
    public bool RequiresGroundTruth => true;

    /// <inheritdoc />
    public Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        
        // Get retrieved document IDs (in ranked order)
        var retrieved = context.GetProperty<IReadOnlyList<string>>(RetrievedDocumentIdsKey);
        if (retrieved is null || retrieved.Count == 0)
        {
            return Task.FromResult(MetricResult.Fail(
                Name, 
                $"Missing required property '{RetrievedDocumentIdsKey}'. Set context.Properties[\"{RetrievedDocumentIdsKey}\"] to the ORDERED list of retrieved document IDs.",
                details: new Dictionary<string, object>
                {
                    ["error"] = "missing_retrieved_ids",
                    ["suggestion"] = $"context.SetProperty(\"{RetrievedDocumentIdsKey}\", retrievedIds);"
                }));
        }
        
        // Get relevant document IDs (ground truth)
        var relevant = context.GetProperty<IReadOnlyList<string>>(RelevantDocumentIdsKey);
        if (relevant is null || relevant.Count == 0)
        {
            return Task.FromResult(MetricResult.Fail(
                Name, 
                $"Missing required property '{RelevantDocumentIdsKey}'. Set context.Properties[\"{RelevantDocumentIdsKey}\"] to the list of ground truth relevant document IDs.",
                details: new Dictionary<string, object>
                {
                    ["error"] = "missing_relevant_ids",
                    ["suggestion"] = $"context.SetProperty(\"{RelevantDocumentIdsKey}\", relevantIds);"
                }));
        }
        
        var relevantSet = relevant.ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Find rank of first relevant document (1-indexed)
        var searchLimit = _maxRank ?? retrieved.Count;
        var firstRelevantRank = 0;
        
        for (int i = 0; i < Math.Min(retrieved.Count, searchLimit); i++)
        {
            if (relevantSet.Contains(retrieved[i]))
            {
                firstRelevantRank = i + 1; // 1-indexed
                break;
            }
        }
        
        // Calculate MRR
        var mrr = firstRelevantRank > 0 ? 1.0 / firstRelevantRank : 0.0;
        var score = mrr * 100.0; // Convert to 0-100 scale
        
        var details = new Dictionary<string, object>
        {
            ["max_rank"] = _maxRank.HasValue ? (object)_maxRank.Value : "unlimited",
            ["first_relevant_rank"] = firstRelevantRank,
            ["mrr"] = mrr,
            ["retrieved_count"] = retrieved.Count,
            ["relevant_count"] = relevantSet.Count
        };
        
        if (firstRelevantRank > 0)
        {
            details["first_relevant_doc"] = retrieved[firstRelevantRank - 1];
        }
        
        string explanation;
        if (firstRelevantRank == 0)
        {
            explanation = _maxRank.HasValue 
                ? $"No relevant document found in top {_maxRank}" 
                : "No relevant document found in results";
        }
        else if (firstRelevantRank == 1)
        {
            explanation = "First result was relevant (perfect ranking!)";
        }
        else
        {
            explanation = $"First relevant document at position {firstRelevantRank} (MRR = 1/{firstRelevantRank} = {mrr:F3})";
        }
        
        // Pass if first relevant is in top 3
        var passed = firstRelevantRank > 0 && firstRelevantRank <= 3;
        
        return Task.FromResult(new MetricResult
        {
            MetricName = Name,
            Score = score,
            Passed = passed,
            Explanation = explanation,
            Details = details
        });
    }
    
    /// <summary>
    /// Calculates MRR directly from document ID lists.
    /// </summary>
    /// <param name="retrievedDocIds">Ordered list of retrieved document IDs.</param>
    /// <param name="relevantDocIds">List of ground truth relevant document IDs.</param>
    /// <returns>MRR score between 0.0 and 1.0.</returns>
    public double CalculateMRR(IReadOnlyList<string> retrievedDocIds, IReadOnlyList<string> relevantDocIds)
    {
        if (relevantDocIds.Count == 0) return 0.0;
        
        var relevantSet = relevantDocIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var searchLimit = _maxRank ?? retrievedDocIds.Count;
        
        for (int i = 0; i < Math.Min(retrievedDocIds.Count, searchLimit); i++)
        {
            if (relevantSet.Contains(retrievedDocIds[i]))
            {
                return 1.0 / (i + 1);
            }
        }
        
        return 0.0;
    }
}
