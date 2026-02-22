// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Testing;

/// <summary>
/// Result of evaluating conversation completeness.
/// </summary>
public class ConversationMetricResult
{
    /// <summary>Name of the metric.</summary>
    public string MetricName { get; init; } = "ConversationCompleteness";
    
    /// <summary>Overall score from 0.0 to 1.0.</summary>
    public double Score { get; init; }
    
    /// <summary>Whether the metric passed.</summary>
    public bool Passed { get; init; }
    
    /// <summary>Sub-scores for different aspects.</summary>
    public Dictionary<string, double> SubScores { get; init; } = new();
    
    /// <summary>Additional details.</summary>
    public Dictionary<string, object?> Details { get; init; } = new();
}

/// <summary>
/// Metric that evaluates conversation completeness and flow.
/// </summary>
/// <remarks>
/// Measures:
/// - Response rate: Did every user turn get a response?
/// - Tool usage: Were expected tools called?
/// - Flow continuity: Did the conversation maintain coherent context?
/// </remarks>
public class ConversationCompletenessMetric
{
    /// <summary>Name of this metric.</summary>
    public string Name => "ConversationCompleteness";
    
    /// <summary>Description of what this metric measures.</summary>
    public string Description => "Evaluates whether a multi-turn conversation completed successfully with all expected responses and tool calls.";

    /// <summary>
    /// Evaluates a conversation result.
    /// </summary>
    public ConversationMetricResult Evaluate(ConversationResult result)
    {
        var scores = new Dictionary<string, double>();
        var details = new Dictionary<string, object?>();

        // 1. Response Rate: Did every user turn get a response?
        var userTurns = result.TestCase.Turns.Count(t => 
            t.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        var assistantTurns = result.ActualTurns.Count(t => 
            t.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
        
        var responseRate = userTurns > 0 ? Math.Min(1.0, (double)assistantTurns / userTurns) : 1.0;
        scores["ResponseRate"] = responseRate;
        details["UserTurns"] = userTurns;
        details["AssistantTurns"] = assistantTurns;

        // 2. Tool Usage: Were expected tools called?
        double toolScore = 1.0;
        if (result.TestCase.ExpectedTools != null && result.TestCase.ExpectedTools.Count > 0)
        {
            var expectedSet = new HashSet<string>(result.TestCase.ExpectedTools, StringComparer.OrdinalIgnoreCase);
            var calledSet = new HashSet<string>(result.ToolsCalled, StringComparer.OrdinalIgnoreCase);
            var intersection = expectedSet.Intersect(calledSet).Count();
            toolScore = (double)intersection / expectedSet.Count;
            
            details["ExpectedTools"] = result.TestCase.ExpectedTools;
            details["CalledTools"] = result.ToolsCalled;
            details["ToolsMatched"] = intersection;
        }
        scores["ToolUsage"] = toolScore;

        // 3. Duration Compliance
        double durationScore = 1.0;
        if (result.TestCase.MaxDuration.HasValue && result.Duration > result.TestCase.MaxDuration.Value)
        {
            // Penalize based on how much over the limit
            var overageRatio = result.Duration.TotalMilliseconds / result.TestCase.MaxDuration.Value.TotalMilliseconds;
            durationScore = Math.Max(0, 1.0 - (overageRatio - 1.0)); // 0 if 2x over, etc.
        }
        scores["DurationCompliance"] = durationScore;
        details["Duration"] = result.Duration;
        details["MaxDuration"] = result.TestCase.MaxDuration;

        // 4. Error-free execution
        double errorScore = result.Error == null ? 1.0 : 0.0;
        scores["ErrorFree"] = errorScore;
        if (result.Error != null)
        {
            details["Error"] = result.Error;
        }

        // Calculate overall score (weighted average)
        var overallScore = (responseRate * 0.4) + (toolScore * 0.3) + (durationScore * 0.15) + (errorScore * 0.15);

        return new ConversationMetricResult
        {
            MetricName = Name,
            Score = overallScore,
            SubScores = scores,
            Details = details,
            Passed = overallScore >= 0.7 && errorScore == 1.0 // Must be error-free and score >= 70%
        };
    }

    /// <summary>
    /// Evaluates multiple conversation results.
    /// </summary>
    public IReadOnlyList<ConversationMetricResult> EvaluateAll(IEnumerable<ConversationResult> results)
    {
        return results.Select(Evaluate).ToList();
    }

    /// <summary>
    /// Gets aggregate statistics across multiple conversations.
    /// </summary>
    public ConversationAggregateStats GetAggregateStats(IEnumerable<ConversationResult> results)
    {
        var resultList = results.ToList();
        if (resultList.Count == 0)
        {
            return new ConversationAggregateStats();
        }

        var metrics = resultList.Select(Evaluate).ToList();
        
        return new ConversationAggregateStats
        {
            TotalConversations = resultList.Count,
            SuccessfulConversations = resultList.Count(r => r.Success),
            AverageScore = metrics.Average(m => m.Score),
            AverageResponseRate = metrics.Average(m => m.SubScores.GetValueOrDefault("ResponseRate", 1.0)),
            AverageToolUsage = metrics.Average(m => m.SubScores.GetValueOrDefault("ToolUsage", 1.0)),
            AverageDuration = TimeSpan.FromMilliseconds(resultList.Average(r => r.Duration.TotalMilliseconds)),
            ErrorCount = resultList.Count(r => r.Error != null)
        };
    }
}

/// <summary>
/// Aggregate statistics across multiple conversation tests.
/// </summary>
public record ConversationAggregateStats
{
    /// <summary>Total number of conversations run.</summary>
    public int TotalConversations { get; init; }
    
    /// <summary>Number of successful conversations.</summary>
    public int SuccessfulConversations { get; init; }
    
    /// <summary>Success rate (0-1).</summary>
    public double SuccessRate => TotalConversations > 0 
        ? (double)SuccessfulConversations / TotalConversations 
        : 0;
    
    /// <summary>Average overall score.</summary>
    public double AverageScore { get; init; }
    
    /// <summary>Average response rate.</summary>
    public double AverageResponseRate { get; init; }
    
    /// <summary>Average tool usage score.</summary>
    public double AverageToolUsage { get; init; }
    
    /// <summary>Average conversation duration.</summary>
    public TimeSpan AverageDuration { get; init; }
    
    /// <summary>Number of conversations with errors.</summary>
    public int ErrorCount { get; init; }
}
