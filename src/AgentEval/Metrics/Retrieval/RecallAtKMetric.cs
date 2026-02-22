// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Metrics.Retrieval;

/// <summary>
/// Measures Recall@K - the proportion of relevant documents that appear in the top K retrieved results.
/// 
/// Formula: Recall@K = |Relevant ∩ Retrieved@K| / |Relevant|
/// 
/// This is a code-based metric (free - no LLM calls).
/// </summary>
/// <remarks>
/// <para>
/// Recall@K answers: "Of all the relevant documents, how many did we find in the top K?"
/// </para>
/// <para>
/// Use this metric to evaluate your retrieval system's coverage.
/// </para>
/// <para>
/// Required in EvaluationContext.Properties:
/// <list type="bullet">
///   <item>"RetrievedDocumentIds" - IReadOnlyList&lt;string&gt; of document IDs (ordered by rank)</item>
///   <item>"RelevantDocumentIds" - IReadOnlyList&lt;string&gt; of ground truth relevant document IDs</item>
/// </list>
/// </para>
/// </remarks>
public class RecallAtKMetric : IRAGMetric
{
    /// <summary>
    /// Property key for retrieved document IDs in EvaluationContext.Properties.
    /// </summary>
    public const string RetrievedDocumentIdsKey = "RetrievedDocumentIds";
    
    /// <summary>
    /// Property key for relevant document IDs in EvaluationContext.Properties.
    /// </summary>
    public const string RelevantDocumentIdsKey = "RelevantDocumentIds";
    
    private readonly int _k;
    
    /// <summary>
    /// Initializes a new instance of the RecallAtKMetric.
    /// </summary>
    /// <param name="k">Number of top results to consider (default: 10).</param>
    public RecallAtKMetric(int k = 10)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "K must be positive");
        _k = k;
    }

    /// <inheritdoc />
    public string Name => $"code_recall_at_{_k}";

    /// <inheritdoc />
    public string Description => $"Proportion of relevant documents found in top {_k} results";

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
        
        // Get retrieved document IDs
        var retrieved = context.GetProperty<IReadOnlyList<string>>(RetrievedDocumentIdsKey);
        if (retrieved is null || retrieved.Count == 0)
        {
            return Task.FromResult(MetricResult.Fail(
                Name, 
                $"Missing required property '{RetrievedDocumentIdsKey}'. Set context.Properties[\"{RetrievedDocumentIdsKey}\"] to the list of retrieved document IDs.",
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
        
        // Take top K retrieved
        var retrievedAtK = retrieved.Take(_k).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSet = relevant.ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Calculate Recall@K = |Relevant ∩ Retrieved@K| / |Relevant|
        var relevantFound = relevantSet.Count(id => retrievedAtK.Contains(id));
        var recall = (double)relevantFound / relevantSet.Count;
        var score = recall * 100.0; // Convert to 0-100 scale
        
        var details = new Dictionary<string, object>
        {
            ["k"] = _k,
            ["total_relevant"] = relevantSet.Count,
            ["relevant_in_top_k"] = relevantFound,
            ["recall"] = recall,
            ["retrieved_count"] = retrieved.Count,
            ["retrieved_at_k"] = retrievedAtK.ToList(),
            ["relevant_found"] = relevantSet.Where(id => retrievedAtK.Contains(id)).ToList(),
            ["relevant_missed"] = relevantSet.Where(id => !retrievedAtK.Contains(id)).ToList()
        };
        
        var explanation = relevantFound == relevantSet.Count
            ? $"Found all {relevantSet.Count} relevant documents in top {_k}"
            : $"Found {relevantFound} of {relevantSet.Count} relevant documents in top {_k} (missed: {relevantSet.Count - relevantFound})";
        
        // Pass if recall >= 70% (configurable threshold could be added)
        var passed = recall >= 0.7;
        
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
    /// Calculates Recall@K directly from document ID lists.
    /// </summary>
    /// <param name="retrievedDocIds">Ordered list of retrieved document IDs.</param>
    /// <param name="relevantDocIds">List of ground truth relevant document IDs.</param>
    /// <returns>Recall@K score between 0.0 and 1.0.</returns>
    public double CalculateRecall(IReadOnlyList<string> retrievedDocIds, IReadOnlyList<string> relevantDocIds)
    {
        if (relevantDocIds.Count == 0) return 0.0;
        
        var retrievedAtK = retrievedDocIds.Take(_k).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSet = relevantDocIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var relevantFound = relevantSet.Count(id => retrievedAtK.Contains(id));
        return (double)relevantFound / relevantSet.Count;
    }
}
