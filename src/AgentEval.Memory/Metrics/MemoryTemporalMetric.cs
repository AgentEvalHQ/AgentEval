using AgentEval.Core;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// LLM-evaluated metric that measures agent's temporal memory capabilities - 
/// understanding what was known when and temporal reasoning.
/// </summary>
public class MemoryTemporalMetric : IMetric
{
    private readonly ILogger<MemoryTemporalMetric> _logger;

    public MemoryTemporalMetric(ILogger<MemoryTemporalMetric> logger)
    {
        _logger = logger;
    }

    public string Name => "llm_memory_temporal";
    
    public string Description => "Evaluates agent's temporal memory abilities - time-travel queries and temporal reasoning";
    
    public MetricCategory Categories => MetricCategory.LLMEvaluated | MetricCategory.Memory;
    
    public decimal? EstimatedCostPerEvaluation => 0.003m; // Temporal evaluation may be more complex

    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryResult = context.GetProperty<MemoryEvaluationResult>("MemoryEvaluationResult");
            if (memoryResult == null)
            {
                return MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context.");
            }

            // Check if this was a temporal evaluation
            var isTemporalEvaluation = memoryResult.Metadata?.ContainsKey("TemporalEvaluation") == true;
            if (!isTemporalEvaluation)
            {
                return MetricResult.Pass(Name, 0, "Non-temporal scenario - metric not applicable");
            }
            
            // Extract temporal-specific metrics
            var temporalScore = memoryResult.Metadata?.GetValueOrDefault("TemporalScore") as double? ?? memoryResult.OverallScore;
            var temporalAccuracy = memoryResult.Metadata?.GetValueOrDefault("TemporalAccuracy") as double? ?? 0;
            var temporalQueryCount = memoryResult.Metadata?.GetValueOrDefault("TemporalQueryCount") as int? ?? 0;
            
            var passed = temporalScore >= 75; // Slightly lower threshold for complex temporal reasoning
            
            var details = new Dictionary<string, object>
            {
                ["temporal_score"] = temporalScore,
                ["temporal_accuracy"] = temporalAccuracy,
                ["temporal_query_count"] = temporalQueryCount,
                ["overall_score"] = memoryResult.OverallScore,
                ["scenario_name"] = memoryResult.ScenarioName
            };
            
            // Add time range information if available
            if (memoryResult.Metadata?.TryGetValue("TimeRange", out var timeRangeObj) == true)
            {
                details["time_range"] = timeRangeObj;
            }
            
            if (memoryResult.Metadata?.TryGetValue("QueryTimes", out var queryTimesObj) == true)
            {
                details["query_times"] = queryTimesObj;
            }
            
            var explanation = BuildTemporalExplanation(memoryResult, temporalScore, temporalAccuracy, temporalQueryCount);
            
            _logger.LogDebug("Temporal memory evaluation: {Score}% (accuracy: {Accuracy}%, queries: {Count})",
                temporalScore, temporalAccuracy, temporalQueryCount);
            
            return passed
                ? MetricResult.Pass(Name, temporalScore, explanation, details)
                : MetricResult.Fail(Name, explanation, temporalScore, details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating temporal memory metric");
            return MetricResult.Fail(Name, $"Error during temporal memory evaluation: {ex.Message}");
        }
    }

    private static string BuildTemporalExplanation(
        MemoryEvaluationResult memoryResult, 
        double temporalScore, 
        double temporalAccuracy, 
        int temporalQueryCount)
    {
        var explanation = new List<string>
        {
            $"Temporal memory score: {temporalScore:F1}%",
            $"Temporal reasoning accuracy: {temporalAccuracy:F1}%"
        };
        
        if (temporalQueryCount > 0)
        {
            explanation.Add($"Successfully handled {temporalQueryCount} temporal queries");
        }
        
        // Analyze specific temporal capabilities
        var scenarioType = GetTemporalScenarioType(memoryResult.Metadata);
        switch (scenarioType)
        {
            case "time-travel":
                explanation.Add("Evaluated time-travel query capabilities");
                break;
            case "fact-evolution":
                explanation.Add("Evaluated understanding of information changes over time");
                break;
            case "causal-reasoning":
                explanation.Add("Evaluated temporal causality and sequence understanding");
                break;
            default:
                explanation.Add("Evaluated general temporal memory abilities");
                break;
        }
        
        return string.Join(". ", explanation);
    }

    private static string GetTemporalScenarioType(Dictionary<string, object>? metadata)
    {
        if (metadata == null) return "unknown";
        
        if (metadata.ContainsKey("TemporalQuery")) return "time-travel";
        if (metadata.ContainsKey("TemporalEvolution")) return "fact-evolution";
        if (metadata.ContainsKey("CausalReasoning")) return "causal-reasoning";
        if (metadata.ContainsKey("DegradationTest")) return "memory-degradation";
        
        return "general-temporal";
    }
}