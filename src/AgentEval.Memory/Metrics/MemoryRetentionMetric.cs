// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// Code-computed metric that measures how well an agent retains and recalls established facts.
/// Reads scores from <see cref="MemoryEvaluationResult"/> populated by the memory engine.
/// </summary>
public class MemoryRetentionMetric : IMemoryMetric
{
    private readonly ILogger<MemoryRetentionMetric> _logger;

    public MemoryRetentionMetric(ILogger<MemoryRetentionMetric> logger)
    {
        _logger = logger;
    }

    public string Name => "code_memory_retention";

    public string Description => "Evaluates agent's ability to retain and recall established facts";

    public MetricCategory Categories => MetricCategory.CodeBased | MetricCategory.Memory;
    
    public decimal? EstimatedCostPerEvaluation => null; // Code-computed — reads precomputed values, no API cost

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract memory evaluation result from context
            var memoryResult = context.GetProperty<MemoryEvaluationResult>(MemoryEvaluationContextExtensions.MemoryResultKey);
            if (memoryResult == null)
            {
                return Task.FromResult(MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context. " +
                    "This metric requires memory evaluation results."));
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

            return Task.FromResult(passed
                ? MetricResult.Pass(Name, score, explanation, details)
                : MetricResult.Fail(Name, explanation, score, details));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating memory retention metric");
            return Task.FromResult(MetricResult.Fail(Name, $"Error during memory retention evaluation: {ex.Message}"));
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