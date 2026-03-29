using AgentEval.Core;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.Metrics;

/// <summary>
/// LLM-evaluated metric that measures agent's ability to maintain memory accuracy
/// despite noisy, distracting conversations.
/// </summary>
public class MemoryNoiseResilienceMetric : IMemoryMetric
{
    private readonly ILogger<MemoryNoiseResilienceMetric> _logger;

    public MemoryNoiseResilienceMetric(ILogger<MemoryNoiseResilienceMetric> logger)
    {
        _logger = logger;
    }

    public string Name => "code_memory_noise_resilience";
    
    public string Description => "Evaluates memory retention quality in presence of distracting conversation";
    
    public MetricCategory Categories => MetricCategory.CodeBased | MetricCategory.Memory | MetricCategory.Conversation;
    
    public decimal? EstimatedCostPerEvaluation => 0.002m;

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryResult = context.GetProperty<MemoryEvaluationResult>(MemoryEvaluationContextExtensions.MemoryResultKey);
            if (memoryResult == null)
            {
                return Task.FromResult(MetricResult.Fail(Name, "MemoryEvaluationResult not found in evaluation context."));
            }

            // Check if this was a noise resilience test (chatty/buried facts scenario)
            var isNoiseTest = IsNoiseResilienceScenario(memoryResult.ScenarioName, memoryResult.Metadata);
            if (!isNoiseTest)
            {
                return Task.FromResult(MetricResult.Pass(Name, memoryResult.OverallScore,
                    "Non-noise scenario - using general memory score"));
            }

            // Calculate noise resilience score
            var resilienceScore = CalculateNoiseResilience(memoryResult);
            var passed = resilienceScore >= 70; // Higher threshold for noise resilience

            var details = new Dictionary<string, object>
            {
                ["resilience_score"] = resilienceScore,
                ["overall_score"] = memoryResult.OverallScore,
                ["scenario_name"] = memoryResult.ScenarioName,
                ["queries_passed"] = memoryResult.PassedQueries,
                ["total_queries"] = memoryResult.TotalQueries
            };

            // Add noise-specific analysis
            AnalyzeNoisePattern(memoryResult, details);

            var explanation = BuildNoiseResilienceExplanation(memoryResult, resilienceScore, details);

            _logger.LogDebug("Noise resilience evaluation: {Score}% (scenario: {Scenario})",
                resilienceScore, memoryResult.ScenarioName);

            return Task.FromResult(passed
                ? MetricResult.Pass(Name, resilienceScore, explanation, details)
                : MetricResult.Fail(Name, explanation, resilienceScore, details));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating noise resilience metric");
            return Task.FromResult(MetricResult.Fail(Name, $"Error during noise resilience evaluation: {ex.Message}"));
        }
    }

    private static bool IsNoiseResilienceScenario(string scenarioName, Dictionary<string, object>? metadata)
    {
        var lowerName = scenarioName.ToLowerInvariant();
        
        // Check scenario name patterns
        if (lowerName.Contains("chatty") || 
            lowerName.Contains("buried") || 
            lowerName.Contains("noise") ||
            lowerName.Contains("distract"))
        {
            return true;
        }
        
        // Check metadata indicators
        if (metadata != null)
        {
            if (metadata.ContainsKey("NoiseRatio") ||
                metadata.ContainsKey("ChattyConversation") ||
                metadata.ContainsKey("BuriedFacts"))
            {
                return true;
            }
        }
        
        return false;
    }

    private static double CalculateNoiseResilience(MemoryEvaluationResult memoryResult)
    {
        var baseScore = memoryResult.OverallScore;

        // Analyze factors that affect noise resilience
        var totalFacts = memoryResult.FoundFacts.Count + memoryResult.MissingFacts.Count;
        var factAccuracy = totalFacts > 0
            ? memoryResult.FoundFacts.Count / (double)totalFacts
            : 0;
        
        // Penalty for false recalls (forbidden facts found)
        var falseRecallPenalty = memoryResult.ForbiddenFound.Count * 5; // -5% per false recall
        
        // Calculate resilience score (combination of accuracy and consistency)
        var resilienceScore = Math.Max(0, baseScore - falseRecallPenalty);
        
        // Boost score if performance remained high despite noise
        if (baseScore > 85 && memoryResult.ForbiddenFound.Count == 0)
        {
            resilienceScore = Math.Min(100, resilienceScore * 1.05); // 5% bonus for excellent noise handling
        }
        
        return Math.Round(resilienceScore, 1);
    }

    private static void AnalyzeNoisePattern(MemoryEvaluationResult memoryResult, Dictionary<string, object> details)
    {
        // Extract noise characteristics from metadata if available
        if (memoryResult.Metadata != null)
        {
            if (memoryResult.Metadata.TryGetValue("NoiseRatio", out var noiseRatioObj))
            {
                details["noise_ratio"] = noiseRatioObj;
            }
            
            if (memoryResult.Metadata.TryGetValue("TopicChanges", out var topicChangesObj))
            {
                details["topic_changes"] = topicChangesObj;
            }
            
            if (memoryResult.Metadata.TryGetValue("EmotionalDistractors", out var emotionalObj))
            {
                details["emotional_distractors"] = emotionalObj;
            }
        }
        
        // Analyze false positive rate
        var totalExpectedFacts = memoryResult.FoundFacts.Count + memoryResult.MissingFacts.Count;
        var falsePositiveRate = totalExpectedFacts > 0 
            ? (double)memoryResult.ForbiddenFound.Count / totalExpectedFacts * 100 
            : 0;
        details["false_positive_rate"] = falsePositiveRate;
        
        // Analyze signal-to-noise detection
        var signalDetectionRate = totalExpectedFacts > 0 
            ? (double)memoryResult.FoundFacts.Count / totalExpectedFacts * 100 
            : 0;
        details["signal_detection_rate"] = signalDetectionRate;
    }

    private static string BuildNoiseResilienceExplanation(
        MemoryEvaluationResult memoryResult, 
        double resilienceScore, 
        Dictionary<string, object> details)
    {
        var explanation = new List<string>
        {
            $"Noise resilience score: {resilienceScore:F1}%",
            $"Successfully identified {memoryResult.FoundFacts.Count} relevant facts despite distractions"
        };
        
        if (memoryResult.ForbiddenFound.Count > 0)
        {
            explanation.Add($"WARNING: {memoryResult.ForbiddenFound.Count} false recalls due to noise interference");
        }
        else
        {
            explanation.Add("Excellent noise filtering - no false recalls detected");
        }
        
        if (details.TryGetValue("noise_ratio", out var noiseRatio))
        {
            explanation.Add($"Handled {noiseRatio}:1 noise-to-signal ratio");
        }
        
        var querySuccessRate = memoryResult.SuccessRate;
        if (querySuccessRate >= 90)
        {
            explanation.Add("Exceptional performance in noisy environment");
        }
        else if (querySuccessRate >= 70)
        {
            explanation.Add("Good performance despite distractions");
        }
        else
        {
            explanation.Add("Memory performance affected by noise - consider noise reduction strategies");
        }
        
        return string.Join(". ", explanation);
    }
}