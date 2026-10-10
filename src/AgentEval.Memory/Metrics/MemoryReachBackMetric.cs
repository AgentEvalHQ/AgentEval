// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// Code-computed metric that measures agent's ability to reach back into conversation history
/// to recall information from earlier interactions.
/// </summary>
public class MemoryReachBackMetric : IMemoryMetric
{
    private readonly ILogger<MemoryReachBackMetric> _logger;

    public MemoryReachBackMetric(ILogger<MemoryReachBackMetric> logger)
    {
        _logger = logger;
    }

    public string Name => "code_memory_reachback";
    
    public string Description => "Measures conversation depth analysis and ability to recall information from earlier interactions";
    
    public MetricCategory Categories => MetricCategory.CodeBased | MetricCategory.Memory;
    
    public decimal? EstimatedCostPerEvaluation => 0m; // Code-based, no API costs

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryResult = context.GetProperty<MemoryEvaluationResult>(MemoryEvaluationContextExtensions.MemoryResultKey);
            if (memoryResult == null)
            {
                return Task.FromResult(MetricResult.NotMeasured(Name,
                    "MemoryEvaluationResult not found in evaluation context, and this metric reads it: not measured."));
            }

            // Check if this was a reach-back evaluation
            var isReachBackTest = memoryResult.Metadata?.ContainsKey("ReachBackTest") == true;
            if (!isReachBackTest)
            {
                return EvaluateGeneralReachBack(memoryResult);
            }

            return EvaluateSpecificReachBack(memoryResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating memory reach-back metric");
            return Task.FromResult(MetricResult.Fail(Name, $"Error during reach-back evaluation: {ex.Message}"));
        }
    }

    private Task<MetricResult> EvaluateGeneralReachBack(MemoryEvaluationResult memoryResult)
    {
        // For general scenarios, analyze conversation depth based on query success patterns
        var queryResults = memoryResult.QueryResults.ToArray();

        if (queryResults.Length == 0)
        {
            return Task.FromResult(MetricResult.NotMeasured(Name,
                "The scenario asked no questions, so reach-back capability was not tested: not measured."));
        }

        // Simple heuristic: if agent can answer multiple queries about established facts,
        // it demonstrates reach-back capability
        // Over the measured questions only; with none measured there is no score to give.
        var measured = queryResults.Where(r => r.Measured).ToArray();
        if (measured.Length == 0)
        {
            return Task.FromResult(MetricResult.NotMeasured(Name,
                "Not measured: the judge produced no score for any query."));
        }
        var successfulReachBack = measured.Count(r => r.Passed);
        var reachBackScore = (double)successfulReachBack / measured.Length * 100;
        var notMeasured = queryResults.Length - measured.Length;

        var details = new Dictionary<string, object>
        {
            ["queries_analyzed"] = measured.Length,
            ["queries_not_measured"] = notMeasured,
            ["successful_reachback"] = successfulReachBack,
            ["reachback_score"] = reachBackScore,
            ["analysis_type"] = "general"
        };

        var explanation = $"General reach-back analysis: {successfulReachBack}/{measured.Length} " +
                         $"measured queries successfully retrieved information from memory ({reachBackScore:F1}%)" +
                         (notMeasured > 0 ? $"; {notMeasured} not measured (the judge produced no score)" : "");

        return Task.FromResult(reachBackScore >= 70
            ? MetricResult.Pass(Name, reachBackScore, explanation, details)
            : MetricResult.Fail(Name, explanation, reachBackScore, details));
    }

    private Task<MetricResult> EvaluateSpecificReachBack(MemoryEvaluationResult memoryResult)
    {
        // For specific reach-back tests, use metadata to determine depth and degradation
        var maxDepth = memoryResult.Metadata?.GetValueOrDefault("MaxDepth") as int? ?? 0;
        var depthAnalysis = memoryResult.Metadata?.GetValueOrDefault("DepthAnalysis") as Dictionary<string, object>;
        var degradationCurve = memoryResult.Metadata?.GetValueOrDefault("DegradationCurve") as double[];
        
        var reachBackScore = CalculateReachBackScore(memoryResult, maxDepth, degradationCurve);
        var passed = reachBackScore >= 60; // Lower threshold for deep reach-back
        
        var details = new Dictionary<string, object>
        {
            ["max_depth"] = maxDepth,
            ["reachback_score"] = reachBackScore,
            ["overall_score"] = memoryResult.OverallScore,
            ["analysis_type"] = "specific"
        };
        
        if (degradationCurve != null)
        {
            details["degradation_curve"] = degradationCurve;
            details["degradation_pattern"] = AnalyzeDegradationPattern(degradationCurve);
        }
        
        if (depthAnalysis != null)
        {
            details["depth_analysis"] = depthAnalysis;
        }
        
        var explanation = BuildReachBackExplanation(reachBackScore, maxDepth, degradationCurve);
        
        _logger.LogDebug("Reach-back evaluation: {Score}% (max depth: {Depth})", 
            reachBackScore, maxDepth);
        
        return Task.FromResult(passed
            ? MetricResult.Pass(Name, reachBackScore, explanation, details)
            : MetricResult.Fail(Name, explanation, reachBackScore, details));
    }

    private static double CalculateReachBackScore(
        MemoryEvaluationResult memoryResult, 
        int maxDepth, 
        double[]? degradationCurve)
    {
        var baseScore = memoryResult.OverallScore;
        
        // Adjust score based on conversation depth
        var depthFactor = maxDepth > 0 ? Math.Min(1.0, maxDepth / 50.0) : 0.5; // Normalize to 50 turns
        
        // Analyze degradation pattern if available
        var degradationFactor = 1.0;
        if (degradationCurve != null && degradationCurve.Length > 1)
        {
            // Calculate how gradually the performance degrades
            var avgDegradation = degradationCurve.Skip(1).Average();
            degradationFactor = avgDegradation / 100.0; // Convert percentage to factor
        }
        
        // Calculate composite reach-back score
        var reachBackScore = baseScore * depthFactor * degradationFactor;
        
        return Math.Round(reachBackScore, 1);
    }

    private static string AnalyzeDegradationPattern(double[] degradationCurve)
    {
        if (degradationCurve.Length < 2) return "insufficient-data";
        
        var firstScore = degradationCurve[0];
        var lastScore = degradationCurve[^1];
        var totalDrop = firstScore - lastScore;
        
        if (totalDrop < 10) return "stable"; // Less than 10% drop
        if (totalDrop < 25) return "gradual"; // 10-25% drop
        if (totalDrop < 50) return "moderate"; // 25-50% drop
        return "steep"; // More than 50% drop
    }

    private static string BuildReachBackExplanation(
        double reachBackScore, 
        int maxDepth, 
        double[]? degradationCurve)
    {
        var explanation = new List<string>
        {
            $"Reach-back capability: {reachBackScore:F1}%"
        };
        
        if (maxDepth > 0)
        {
            explanation.Add($"Maximum conversation depth tested: {maxDepth} turns");
        }
        
        if (degradationCurve != null)
        {
            var pattern = AnalyzeDegradationPattern(degradationCurve);
            explanation.Add($"Memory degradation pattern: {pattern}");
            
            var firstScore = degradationCurve[0];
            var lastScore = degradationCurve[^1];
            explanation.Add($"Performance range: {lastScore:F1}% to {firstScore:F1}%");
        }
        
        return string.Join(". ", explanation);
    }
}