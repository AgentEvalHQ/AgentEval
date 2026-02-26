// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using AgentEval.Core;

// Alias to avoid conflict with AgentEval.Core.EvaluationContext
using MicrosoftEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MicrosoftIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Adapters;

/// <summary>
/// Adapter that wraps Microsoft.Extensions.AI.Evaluation evaluators as AgentEval IMetric.
/// 
/// This allows using Microsoft's official quality evaluators (Fluency, Coherence, Relevance, etc.)
/// within the AgentEval framework while maintaining score normalization (1-5 → 0-100).
/// </summary>
public class MicrosoftEvaluatorAdapter : IMetric
{
    private readonly MicrosoftIEvaluator _evaluator;
    private readonly IChatClient _chatClient;
    private readonly string _name;
    private readonly string _description;
    private readonly double _passingThreshold;
    
    /// <inheritdoc />
    public string Name => _name;
    
    /// <inheritdoc />
    public string Description => _description;
    
    /// <summary>
    /// Creates an adapter for a Microsoft evaluator.
    /// </summary>
    /// <param name="evaluator">The Microsoft evaluator to wrap.</param>
    /// <param name="chatClient">The chat client for the evaluator to use.</param>
    /// <param name="name">Override name (defaults to evaluator type name).</param>
    /// <param name="description">Override description.</param>
    /// <param name="passingThreshold">Passing score threshold (0-100 scale).</param>
    public MicrosoftEvaluatorAdapter(
        MicrosoftIEvaluator evaluator,
        IChatClient chatClient,
        string? name = null,
        string? description = null,
        double passingThreshold = EvaluationDefaults.PassingScoreThreshold)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _name = name ?? evaluator.GetType().Name.Replace("Evaluator", "");
        _description = description ?? $"Microsoft.Extensions.AI.Evaluation {_name} metric.";
        _passingThreshold = passingThreshold;
    }
    
    /// <inheritdoc />
    public async Task<MetricResult> EvaluateAsync(
        Core.EvaluationContext context, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Build chat messages for Microsoft evaluator format
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, context.Input)
            };
            
            // Add context if available (for grounded evaluations)
            if (!string.IsNullOrEmpty(context.Context))
            {
                messages.Insert(0, new ChatMessage(ChatRole.System, 
                    $"Use the following context to answer:\n\n{context.Context}"));
            }
            
            // Create response object
            var response = new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, context.Output)]);
            
            // Create chat configuration
            var chatConfig = new ChatConfiguration(_chatClient);
            
            // Build additional context for evaluators that need it
            var additionalContext = new List<MicrosoftEvaluationContext>();
            
            // Run the Microsoft evaluator
            var result = await _evaluator.EvaluateAsync(
                messages, 
                response, 
                chatConfig,
                additionalContext: additionalContext,
                cancellationToken: cancellationToken);
            
            // Extract and normalize the score
            var metrics = result.Metrics;
            if (metrics.Count == 0)
            {
                return MetricResult.Fail(Name, "Evaluator returned no metrics.");
            }
            
            // Get the first (usually only) metric
            var firstMetric = metrics.First();
            var metricValue = firstMetric.Value;
            
            double score;
            string? reasoning = null;
            
            if (metricValue is NumericMetric numericMetric)
            {
                // Microsoft uses 1-5 scale, convert to 0-100
                // Handle nullable Value
                score = ScoreNormalizer.FromOneToFive(numericMetric.Value ?? 1.0);
                reasoning = numericMetric.Interpretation?.ToString();
            }
            else if (metricValue is BooleanMetric boolMetric)
            {
                score = boolMetric.Value == true ? 100 : 0;
                reasoning = boolMetric.Interpretation?.ToString();
            }
            else
            {
                return MetricResult.Fail(Name, $"Unsupported metric type: {metricValue?.GetType().Name}");
            }
            
            var metadata = new Dictionary<string, object>
            {
                ["microsoftMetricName"] = firstMetric.Key,
                ["rawScore"] = metricValue is NumericMetric nm ? (nm.Value ?? 0.0) : (metricValue is BooleanMetric bm ? (bm.Value == true ? 5 : 1) : 0),
                ["interpretation"] = ScoreNormalizer.Interpret(score)
            };
            
            if (!string.IsNullOrEmpty(reasoning))
            {
                metadata["reasoning"] = reasoning;
            }
            
            if (score >= _passingThreshold)
            {
                return MetricResult.Pass(Name, score, reasoning ?? $"Score: {score:F0}/100", metadata);
            }
            else
            {
                return MetricResult.Fail(Name, reasoning ?? $"Score below threshold: {score:F0}/100", score, metadata);
            }
        }
        catch (Exception ex)
        {
            return MetricResult.Fail(
                Name, 
                $"Microsoft evaluator failed: {ex.Message}",
                details: new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // FACTORY METHODS FOR COMMON EVALUATORS
    // ═══════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Creates a Fluency evaluator (grammar, vocabulary, sentence structure).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateFluencyEvaluator(IChatClient chatClient)
        => new(new FluencyEvaluator(), chatClient, 
            "Fluency", 
            "Evaluates linguistic fluency including grammar, vocabulary, and sentence structure.");
    
    /// <summary>
    /// Creates a Coherence evaluator (logical flow and organization).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateCoherenceEvaluator(IChatClient chatClient)
        => new(new CoherenceEvaluator(), chatClient,
            "Coherence",
            "Evaluates logical flow, organization, and consistency of the response.");
    
    /// <summary>
    /// Creates a Relevance evaluator (answer addresses the question).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateRelevanceEvaluator(IChatClient chatClient)
        => new(new RelevanceEvaluator(), chatClient,
            "Relevance",
            "Evaluates how well the response addresses the user's question.");
    
    /// <summary>
    /// Creates a Groundedness evaluator (answer is grounded in context).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateGroundednessEvaluator(IChatClient chatClient)
        => new(new GroundednessEvaluator(), chatClient,
            "Groundedness",
            "Evaluates whether the response is grounded in the provided context (no hallucinations).");
    
    /// <summary>
    /// Creates an Equivalence evaluator (answer matches ground truth).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateEquivalenceEvaluator(IChatClient chatClient)
        => new(new EquivalenceEvaluator(), chatClient,
            "Equivalence",
            "Evaluates semantic equivalence between the response and expected answer.");
    
    /// <summary>
    /// Creates a Completeness evaluator (answer is complete).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateCompletenessEvaluator(IChatClient chatClient)
        => new(new CompletenessEvaluator(), chatClient,
            "Completeness",
            "Evaluates whether the response fully addresses all aspects of the question.");
}

/// <summary>
/// Extension methods for easily adding Microsoft evaluators to an evaluation suite.
/// </summary>
public static class MicrosoftEvaluatorExtensions
{
    /// <summary>
    /// Create all standard Microsoft quality evaluators.
    /// </summary>
    public static IEnumerable<IMetric> CreateAllQualityEvaluators(IChatClient chatClient)
    {
        yield return MicrosoftEvaluatorAdapter.CreateFluencyEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateCoherenceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateRelevanceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateGroundednessEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateEquivalenceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateCompletenessEvaluator(chatClient);
    }
}
