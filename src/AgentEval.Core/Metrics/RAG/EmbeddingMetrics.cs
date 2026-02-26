// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Embeddings;

namespace AgentEval.Metrics.RAG;

/// <summary>
/// Base class for embedding-based similarity metrics.
/// Reduces code duplication by providing common evaluation logic.
/// </summary>
public abstract class EmbeddingBasedMetric : IRAGMetric
{
    protected readonly IAgentEvalEmbeddings Embeddings;
    protected readonly double PassingThreshold;
    
    /// <inheritdoc />
    public abstract string Name { get; }
    
    /// <inheritdoc />
    public abstract string Description { get; }
    
    /// <inheritdoc />
    public abstract bool RequiresContext { get; }
    
    /// <inheritdoc />
    public abstract bool RequiresGroundTruth { get; }
    
    /// <summary>
    /// Creates a new embedding-based metric.
    /// </summary>
    /// <param name="embeddings">The embedding generator to use.</param>
    /// <param name="passingThreshold">Score threshold for passing.</param>
    protected EmbeddingBasedMetric(IAgentEvalEmbeddings embeddings, double passingThreshold)
    {
        Embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        PassingThreshold = passingThreshold;
    }
    
    /// <summary>
    /// Get the two texts to compare for similarity.
    /// </summary>
    /// <param name="context">The evaluation context.</param>
    /// <returns>A tuple of (text1, text2) to compare, or (null, error message) if validation fails.</returns>
    protected abstract (string? Text1, string? Text2, string? ValidationError) GetTextsToCompare(EvaluationContext context);
    
    /// <summary>
    /// Get the message to display when comparison passes.
    /// </summary>
    protected abstract string GetPassMessage(double similarity);
    
    /// <summary>
    /// Get the message to display when comparison fails.
    /// </summary>
    protected abstract string GetFailMessage(double similarity);
    
    /// <summary>
    /// Get additional metadata to include in the result.
    /// </summary>
    protected virtual Dictionary<string, object> GetAdditionalMetadata(EvaluationContext context, double similarity, double score)
    {
        return new Dictionary<string, object>
        {
            ["cosineSimilarity"] = similarity,
            ["interpretation"] = ScoreNormalizer.Interpret(score)
        };
    }
    
    /// <inheritdoc />
    public async Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        var (text1, text2, validationError) = GetTextsToCompare(context);
        
        if (validationError != null)
        {
            return MetricResult.Fail(Name, validationError);
        }
        
        try
        {
            var texts = new[] { text1!, text2! };
            var embeddings = await Embeddings.GetEmbeddingsAsync(texts, cancellationToken)
                .ConfigureAwait(false);
            
            if (embeddings.Count < 2)
            {
                return MetricResult.Fail(Name, "Failed to generate embeddings.");
            }
            
            var similarity = EmbeddingSimilarity.CosineSimilarity(embeddings[0], embeddings[1]);
            var score = ScoreNormalizer.FromSimilarity(similarity);
            var metadata = GetAdditionalMetadata(context, similarity, score);
            
            if (score >= PassingThreshold)
            {
                return MetricResult.Pass(Name, score, GetPassMessage(similarity), metadata);
            }
            else
            {
                return MetricResult.Fail(Name, GetFailMessage(similarity), score, metadata);
            }
        }
        catch (Exception ex)
        {
            return MetricResult.Fail(
                Name, 
                $"Embedding generation failed: {ex.Message}",
                details: new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }
}

/// <summary>
/// Measures semantic similarity between the generated answer and ground truth using embeddings.
/// 
/// This is a fast, cost-effective alternative to LLM-based AnswerCorrectnessMetric.
/// Uses cosine similarity of text embeddings rather than LLM prompts.
/// </summary>
/// <remarks>
/// Best used when:
/// - You need fast, cheap evaluations at scale
/// - You have ground truth answers available
/// - Semantic similarity (meaning) is more important than exact wording
/// 
/// Limitations:
/// - Cannot detect subtle factual errors (e.g., wrong numbers)
/// - May miss negation differences ("is" vs "is not")
/// - Requires embedding model in addition to LLM
/// </remarks>
public class AnswerSimilarityMetric : EmbeddingBasedMetric
{
    /// <inheritdoc />
    public override string Name => "embed_answer_similarity";
    
    /// <inheritdoc />
    public override string Description => "Measures semantic similarity between the answer and ground truth using embeddings (fast, no LLM call).";
    
    /// <inheritdoc />
    public override bool RequiresContext => false;
    
    /// <inheritdoc />
    public override bool RequiresGroundTruth => true;
    
    /// <summary>
    /// Creates a new embedding-based answer similarity metric.
    /// </summary>
    /// <param name="embeddings">The embedding generator to use.</param>
    /// <param name="passingThreshold">Score threshold for passing (default: 70).</param>
    public AnswerSimilarityMetric(
        IAgentEvalEmbeddings embeddings, 
        double passingThreshold = EvaluationDefaults.PassingScoreThreshold)
        : base(embeddings, passingThreshold) { }
    
    /// <inheritdoc />
    protected override (string? Text1, string? Text2, string? ValidationError) GetTextsToCompare(EvaluationContext context)
    {
        if (string.IsNullOrEmpty(context.GroundTruth))
            return (null, null, "Answer similarity requires ground truth to be provided.");
        
        if (string.IsNullOrEmpty(context.Output))
            return (null, null, "Answer similarity requires an output to evaluate.");
        
        return (context.GroundTruth, context.Output, null);
    }
    
    /// <inheritdoc />
    protected override string GetPassMessage(double similarity) => 
        $"High semantic similarity ({similarity:P1}) to ground truth.";
    
    /// <inheritdoc />
    protected override string GetFailMessage(double similarity) => 
        $"Low semantic similarity ({similarity:P1}) to ground truth.";
    
    /// <inheritdoc />
    protected override Dictionary<string, object> GetAdditionalMetadata(EvaluationContext context, double similarity, double score)
    {
        var metadata = base.GetAdditionalMetadata(context, similarity, score);
        metadata["groundTruthLength"] = context.GroundTruth?.Length ?? 0;
        metadata["outputLength"] = context.Output?.Length ?? 0;
        return metadata;
    }
}

/// <summary>
/// Measures semantic similarity between the response and the retrieved context.
/// Useful for evaluating whether the answer actually uses the provided context.
/// </summary>
public class ResponseContextSimilarityMetric : EmbeddingBasedMetric
{
    /// <inheritdoc />
    public override string Name => "embed_response_context";
    
    /// <inheritdoc />
    public override string Description => "Measures how semantically similar the response is to the retrieved context (grounding check).";
    
    /// <inheritdoc />
    public override bool RequiresContext => true;
    
    /// <inheritdoc />
    public override bool RequiresGroundTruth => false;
    
    /// <summary>
    /// Creates a new response-context similarity metric.
    /// </summary>
    /// <param name="embeddings">The embedding generator to use.</param>
    /// <param name="passingThreshold">Score threshold for passing (default: 50 - lower threshold as responses may extend beyond context).</param>
    public ResponseContextSimilarityMetric(
        IAgentEvalEmbeddings embeddings, 
        double passingThreshold = 50.0)
        : base(embeddings, passingThreshold) { }
    
    /// <inheritdoc />
    protected override (string? Text1, string? Text2, string? ValidationError) GetTextsToCompare(EvaluationContext context)
    {
        if (string.IsNullOrEmpty(context.Context))
            return (null, null, "Response-context similarity requires context to be provided.");
        
        if (string.IsNullOrEmpty(context.Output))
            return (null, null, "Response-context similarity requires an output to evaluate.");
        
        return (context.Context, context.Output, null);
    }
    
    /// <inheritdoc />
    protected override string GetPassMessage(double similarity) => 
        $"Response is semantically grounded in context ({similarity:P1} similarity).";
    
    /// <inheritdoc />
    protected override string GetFailMessage(double similarity) => 
        $"Response may not be grounded in context ({similarity:P1} similarity).";
}

/// <summary>
/// Measures semantic similarity between the question and the retrieved context.
/// Useful for evaluating retrieval quality without LLM calls.
/// </summary>
public class QueryContextSimilarityMetric : EmbeddingBasedMetric
{
    /// <inheritdoc />
    public override string Name => "embed_query_context";
    
    /// <inheritdoc />
    public override string Description => "Measures how semantically relevant the retrieved context is to the query.";
    
    /// <inheritdoc />
    public override bool RequiresContext => true;
    
    /// <inheritdoc />
    public override bool RequiresGroundTruth => false;
    
    /// <summary>
    /// Creates a new query-context similarity metric.
    /// </summary>
    public QueryContextSimilarityMetric(
        IAgentEvalEmbeddings embeddings, 
        double passingThreshold = 50.0)
        : base(embeddings, passingThreshold) { }
    
    /// <inheritdoc />
    protected override (string? Text1, string? Text2, string? ValidationError) GetTextsToCompare(EvaluationContext context)
    {
        if (string.IsNullOrEmpty(context.Context))
            return (null, null, "Query-context similarity requires context to be provided.");
        
        return (context.Input, context.Context, null);
    }
    
    /// <inheritdoc />
    protected override string GetPassMessage(double similarity) => 
        $"Retrieved context is relevant to query ({similarity:P1} similarity).";
    
    /// <inheritdoc />
    protected override string GetFailMessage(double similarity) => 
        $"Retrieved context may not be relevant to query ({similarity:P1} similarity).";
}
