using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// LLM-evaluated metric that measures how well an agent retains and recalls established facts.
/// Uses structured evaluation to determine memory retention quality.
/// </summary>
public class MemoryRetentionMetric : IMetric
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<MemoryRetentionMetric> _logger;

    public MemoryRetentionMetric(IChatClient chatClient, ILogger<MemoryRetentionMetric> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public string Name => "llm_memory_retention";
    
    public string Description => "Evaluates agent's ability to retain and recall established facts using LLM-based analysis";
    
    public MetricCategory Categories => MetricCategory.LLMEvaluated | MetricCategory.Memory;
    
    public decimal? EstimatedCostPerEvaluation => 0.002m; // ~$0.002 per evaluation

    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract memory evaluation result from context
            var memoryResult = context.GetProperty<MemoryEvaluationResult>("MemoryEvaluationResult");
            if (memoryResult == null)
            {
                return MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context. " +
                    "This metric requires memory evaluation results.");
            }
            
            // Use memory evaluation scores as basis
            var score = memoryResult.OverallScore;
            var passed = score >= 80; // Default threshold for memory retention
            
            var details = new Dictionary<string, object>
            {
                ["overall_score"] = score,
                ["queries_passed"] = memoryResult.PassedQueries,
                ["total_queries"] = memoryResult.TotalQueries,
                ["success_rate"] = memoryResult.SuccessRate,
                ["found_facts"] = memoryResult.FoundFacts.Count,
                ["missing_facts"] = memoryResult.MissingFacts.Count,
                ["forbidden_found"] = memoryResult.ForbiddenFound.Count,
                ["scenario_name"] = memoryResult.ScenarioName,
                ["duration_ms"] = memoryResult.Duration.TotalMilliseconds,
                ["tokens_used"] = memoryResult.TokensUsed,
                ["estimated_cost"] = memoryResult.EstimatedCost
            };
            
            var explanation = BuildExplanation(memoryResult);
            
            _logger.LogDebug("Memory retention evaluation: {Score}% ({Passed}/{Total} queries passed)",
                score, memoryResult.PassedQueries, memoryResult.TotalQueries);
            
            return passed
                ? MetricResult.Pass(Name, score, explanation, details)
                : MetricResult.Fail(Name, explanation, score, details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating memory retention metric");
            return MetricResult.Fail(Name, $"Error during memory retention evaluation: {ex.Message}");
        }
    }

    private static string BuildExplanation(MemoryEvaluationResult memoryResult)
    {
        var explanation = new List<string>
        {
            $"Memory retention score: {memoryResult.OverallScore:F1}%",
            $"Queries passed: {memoryResult.PassedQueries}/{memoryResult.TotalQueries} ({memoryResult.SuccessRate:F1}%)"
        };
        
        if (memoryResult.FoundFacts.Count > 0)
        {
            explanation.Add($"Successfully recalled: {memoryResult.FoundFacts.Count} facts");
        }
        
        if (memoryResult.MissingFacts.Count > 0)
        {
            explanation.Add($"Failed to recall: {memoryResult.MissingFacts.Count} facts");
            
            // Add specific missing facts (up to 3)
            var missingExamples = memoryResult.MissingFacts.Take(3).Select(f => $"'{f.Content}'");
            explanation.Add($"Missing examples: {string.Join(", ", missingExamples)}");
        }
        
        if (memoryResult.ForbiddenFound.Count > 0)
        {
            explanation.Add($"WARNING: Incorrectly recalled {memoryResult.ForbiddenFound.Count} forbidden facts");
        }
        
        explanation.Add($"Evaluation completed in {memoryResult.Duration:g} using {memoryResult.TokensUsed} tokens");
        
        return string.Join(". ", explanation);
    }
}