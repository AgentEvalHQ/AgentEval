// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// Code-computed metric that evaluates the fidelity of memory compression/reduction processes.
/// Measures how well important information is preserved when conversation history is condensed.
/// </summary>
public class MemoryReducerFidelityMetric : IMemoryMetric
{
    private readonly ILogger<MemoryReducerFidelityMetric> _logger;

    public MemoryReducerFidelityMetric(ILogger<MemoryReducerFidelityMetric> logger)
    {
        _logger = logger;
    }

    public string Name => "code_memory_reducer_fidelity";
    
    public string Description => "Evaluates information preservation quality during memory compression/reduction";
    
    public MetricCategory Categories => MetricCategory.CodeBased | MetricCategory.Memory;
    
    public decimal? EstimatedCostPerEvaluation => 0m; // Code-based computation

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryResult = context.GetProperty<MemoryEvaluationResult>(MemoryEvaluationContextExtensions.MemoryResultKey);
            if (memoryResult == null)
            {
                return Task.FromResult(MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context."));
            }

            // Check if this was a reducer fidelity test
            var isReducerTest = memoryResult.Metadata?.ContainsKey("ReducerFidelityTest") == true;
            if (!isReducerTest)
            {
                return EvaluateGeneralFidelity(memoryResult);
            }

            return EvaluateSpecificReducerFidelity(memoryResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating reducer fidelity metric");
            return Task.FromResult(MetricResult.Fail(Name, $"Error during reducer fidelity evaluation: {ex.Message}"));
        }
    }

    private Task<MetricResult> EvaluateGeneralFidelity(MemoryEvaluationResult memoryResult)
    {
        // For general scenarios, evaluate fidelity based on preservation of important facts
        var totalExpectedFacts = memoryResult.FoundFacts.Count + memoryResult.MissingFacts.Count;

        if (totalExpectedFacts == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No facts to evaluate for reduction fidelity"));
        }

        var preservationRate = (double)memoryResult.FoundFacts.Count / totalExpectedFacts * 100;

        // Analyze fact criticality (if metadata available)
        var criticalFactsPreserved = AnalyzeCriticalFactPreservation(memoryResult);

        var fidelityScore = CalculateGeneralFidelityScore(preservationRate, criticalFactsPreserved);
        var passed = fidelityScore >= 75;

        var details = new Dictionary<string, object>
        {
            ["preservation_rate"] = preservationRate,
            ["fidelity_score"] = fidelityScore,
            ["facts_preserved"] = memoryResult.FoundFacts.Count,
            ["facts_lost"] = memoryResult.MissingFacts.Count,
            ["critical_facts_preserved"] = criticalFactsPreserved,
            ["evaluation_type"] = "general"
        };

        var explanation = $"General memory fidelity: {fidelityScore:F1}% " +
                         $"({memoryResult.FoundFacts.Count}/{totalExpectedFacts} facts preserved, " +
                         $"{criticalFactsPreserved}% critical facts retained)";

        return Task.FromResult(passed
            ? MetricResult.Pass(Name, fidelityScore, explanation, details)
            : MetricResult.Fail(Name, explanation, fidelityScore, details));
    }

    private Task<MetricResult> EvaluateSpecificReducerFidelity(MemoryEvaluationResult memoryResult)
    {
        // Extract reducer-specific metrics from metadata
        var compressionRatio = memoryResult.Metadata?.GetValueOrDefault("CompressionRatio") as double? ?? 0;
        var informationLoss = memoryResult.Metadata?.GetValueOrDefault("InformationLoss") as double? ?? 0;
        var keyFactRetention = memoryResult.Metadata?.GetValueOrDefault("KeyFactRetention") as double? ?? 0;
        var reducedSize = memoryResult.Metadata?.GetValueOrDefault("ReducedSize") as int? ?? 0;
        var originalSize = memoryResult.Metadata?.GetValueOrDefault("OriginalSize") as int? ?? 0;
        
        var fidelityScore = CalculateSpecificFidelityScore(
            memoryResult.OverallScore, 
            compressionRatio, 
            informationLoss, 
            keyFactRetention
        );
        
        var passed = fidelityScore >= 70; // Slightly lower threshold for explicit compression
        
        var details = new Dictionary<string, object>
        {
            ["fidelity_score"] = fidelityScore,
            ["compression_ratio"] = compressionRatio,
            ["information_loss"] = informationLoss,
            ["key_fact_retention"] = keyFactRetention,
            ["reduced_size"] = reducedSize,
            ["original_size"] = originalSize,
            ["evaluation_type"] = "specific"
        };
        
        if (originalSize > 0 && reducedSize > 0)
        {
            var actualCompressionRatio = (double)originalSize / reducedSize;
            details["actual_compression_ratio"] = actualCompressionRatio;
        }
        
        var explanation = BuildReducerFidelityExplanation(
            fidelityScore, compressionRatio, informationLoss, keyFactRetention
        );
        
        _logger.LogDebug("Reducer fidelity evaluation: {Score}% (compression: {Ratio:F1}x, loss: {Loss:F1}%)",
            fidelityScore, compressionRatio, informationLoss);
        
        return Task.FromResult(passed
            ? MetricResult.Pass(Name, fidelityScore, explanation, details)
            : MetricResult.Fail(Name, explanation, fidelityScore, details));
    }

    private static double AnalyzeCriticalFactPreservation(MemoryEvaluationResult memoryResult)
    {
        // Analyze preservation of high-importance facts
        var totalFacts = memoryResult.FoundFacts.Concat(memoryResult.MissingFacts);
        var criticalFacts = totalFacts.Where(f => f.Importance >= 80).ToArray();
        
        if (criticalFacts.Length == 0)
        {
            return 100; // No critical facts to preserve
        }
        
        var criticalFactsFound = memoryResult.FoundFacts.Count(f => f.Importance >= 80);
        return (double)criticalFactsFound / criticalFacts.Length * 100;
    }

    private static double CalculateGeneralFidelityScore(double preservationRate, double criticalFactsPreserved)
    {
        // Weight critical facts more heavily
        var weightedScore = (preservationRate * 0.7) + (criticalFactsPreserved * 0.3);
        return Math.Round(weightedScore, 1);
    }

    private static double CalculateSpecificFidelityScore(
        double overallScore,
        double compressionRatio,
        double informationLoss,
        double keyFactRetention)
    {
        // Start with overall memory score
        var baseScore = overallScore;
        
        // Adjust for compression efficiency
        var compressionFactor = compressionRatio > 1 ? Math.Min(1.2, compressionRatio / 10.0) : 1.0;
        
        // Penalize for information loss
        var lossPenalty = informationLoss * 0.5; // 50% weight on information loss
        
        // Reward for key fact retention
        var retentionBonus = keyFactRetention > 90 ? 5 : 0; // 5% bonus for excellent retention
        
        var fidelityScore = Math.Max(0, (baseScore * compressionFactor) - lossPenalty + retentionBonus);
        
        return Math.Min(100, Math.Round(fidelityScore, 1));
    }

    private static string BuildReducerFidelityExplanation(
        double fidelityScore,
        double compressionRatio,
        double informationLoss,
        double keyFactRetention)
    {
        var explanation = new List<string>
        {
            $"Reducer fidelity score: {fidelityScore:F1}%"
        };
        
        if (compressionRatio > 0)
        {
            explanation.Add($"Compression ratio: {compressionRatio:F1}x");
        }
        
        if (informationLoss > 0)
        {
            if (informationLoss < 10)
            {
                explanation.Add($"Minimal information loss: {informationLoss:F1}%");
            }
            else if (informationLoss < 25)
            {
                explanation.Add($"Acceptable information loss: {informationLoss:F1}%");
            }
            else
            {
                explanation.Add($"WARNING: High information loss: {informationLoss:F1}%");
            }
        }
        
        if (keyFactRetention > 0)
        {
            explanation.Add($"Key fact retention: {keyFactRetention:F1}%");
        }
        
        // Add quality assessment
        if (fidelityScore >= 90)
        {
            explanation.Add("Excellent compression with high fidelity");
        }
        else if (fidelityScore >= 75)
        {
            explanation.Add("Good compression quality with acceptable information preservation");
        }
        else
        {
            explanation.Add("Compression quality needs improvement - consider adjusting reduction algorithms");
        }
        
        return string.Join(". ", explanation);
    }
}