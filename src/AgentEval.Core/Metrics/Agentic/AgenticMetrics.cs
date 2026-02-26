// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Metrics.Agentic;

/// <summary>
/// Measures whether the agent selected the correct tools for the task.
/// </summary>
public class ToolSelectionMetric : IAgenticMetric
{
    private readonly IReadOnlyList<string> _expectedTools;
    private readonly bool _strictOrder;
    
    public string Name => "code_tool_selection";
    public string Description => "Measures whether the agent selected the correct tools for the task.";
    public bool RequiresToolUsage => true;
    
    /// <summary>
    /// Create a tool selection metric.
    /// </summary>
    /// <param name="expectedTools">List of expected tool names.</param>
    /// <param name="strictOrder">Whether tools must be called in exact order.</param>
    /// <exception cref="ArgumentNullException">Thrown when expectedTools is null.</exception>
    public ToolSelectionMetric(IEnumerable<string> expectedTools, bool strictOrder = false)
    {
        _expectedTools = (expectedTools ?? throw new ArgumentNullException(nameof(expectedTools))).ToList();
        _strictOrder = strictOrder;
    }
    
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (context.ToolUsage == null || context.ToolUsage.Count == 0)
        {
            if (_expectedTools.Count == 0)
            {
                return Task.FromResult(MetricResult.Pass(Name, 100, "No tools expected and none called."));
            }
            return Task.FromResult(MetricResult.Fail(Name, $"Expected tools [{string.Join(", ", _expectedTools)}] but none were called."));
        }
        
        var calledTools = context.ToolUsage.Calls.Select(c => c.Name.ToLowerInvariant()).ToList();
        var expectedLower = _expectedTools.Select(t => t.ToLowerInvariant()).ToList();
        
        var matched = new List<string>();
        var missed = new List<string>();
        var extra = new List<string>();
        
        // Check for expected tools
        foreach (var expected in expectedLower)
        {
            if (calledTools.Contains(expected))
            {
                matched.Add(expected);
            }
            else
            {
                missed.Add(expected);
            }
        }
        
        // Check for extra tools
        foreach (var called in calledTools.Distinct())
        {
            if (!expectedLower.Contains(called))
            {
                extra.Add(called);
            }
        }
        
        // Calculate score
        var matchScore = _expectedTools.Count > 0 
            ? (double)matched.Count / _expectedTools.Count * 100 
            : 100;
        
        // Penalize extra calls
        var extraPenalty = extra.Count * EvaluationDefaults.ExtraToolPenaltyPercent;
        var finalScore = Math.Max(0, matchScore - extraPenalty);
        
        // Check order if required
        if (_strictOrder && matched.Count == _expectedTools.Count)
        {
            var orderedMatch = true;
            for (int i = 0; i < expectedLower.Count; i++)
            {
                if (i >= calledTools.Count || calledTools[i] != expectedLower[i])
                {
                    orderedMatch = false;
                    break;
                }
            }
            
            if (!orderedMatch)
            {
                finalScore = Math.Max(0, finalScore - EvaluationDefaults.OrderPenaltyPercent);
            }
        }
        
        var metadata = new Dictionary<string, object>
        {
            ["matched"] = matched,
            ["missed"] = missed,
            ["extra"] = extra,
            ["strictOrder"] = _strictOrder
        };
        
        if (finalScore >= EvaluationDefaults.PassingScoreThreshold)
        {
            var msg = missed.Any() 
                ? $"Tool selection mostly correct. Missed: {string.Join(", ", missed)}"
                : "All expected tools were called correctly.";
            return Task.FromResult(MetricResult.Pass(Name, finalScore, msg, metadata));
        }
        else
        {
            var msg = $"Tool selection issues. Missed: [{string.Join(", ", missed)}]. Extra: [{string.Join(", ", extra)}]";
            return Task.FromResult(MetricResult.Fail(Name, msg, finalScore, metadata));
        }
    }
}

/// <summary>
/// Measures whether tool arguments are valid and complete.
/// </summary>
public class ToolArgumentsMetric : IAgenticMetric
{
    private readonly Dictionary<string, HashSet<string>> _requiredArgumentsByTool;
    
    public string Name => "code_tool_arguments";
    public string Description => "Measures whether tool arguments are valid and complete.";
    public bool RequiresToolUsage => true;
    
    /// <summary>
    /// Create a tool arguments metric.
    /// </summary>
    /// <param name="requiredArgumentsByTool">Dictionary mapping tool names to required argument names.</param>
    /// <exception cref="ArgumentNullException">Thrown when requiredArgumentsByTool is null.</exception>
    public ToolArgumentsMetric(Dictionary<string, IEnumerable<string>> requiredArgumentsByTool)
    {
        _ = requiredArgumentsByTool ?? throw new ArgumentNullException(nameof(requiredArgumentsByTool));
        _requiredArgumentsByTool = requiredArgumentsByTool.ToDictionary(
            kvp => kvp.Key.ToLowerInvariant(),
            kvp => kvp.Value.Select(a => a.ToLowerInvariant()).ToHashSet()
        );
    }
    
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (context.ToolUsage == null || context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tools called to validate."));
        }
        
        var totalChecks = 0;
        var passedChecks = 0;
        var issues = new List<string>();
        
        foreach (var call in context.ToolUsage.Calls)
        {
            var toolNameLower = call.Name.ToLowerInvariant();
            
            if (!_requiredArgumentsByTool.TryGetValue(toolNameLower, out var requiredArgs))
            {
                continue; // No requirements defined for this tool
            }
            
            var providedArgs = call.Arguments?.Keys
                .Select(k => k.ToLowerInvariant())
                .ToHashSet() ?? new HashSet<string>();
            
            foreach (var required in requiredArgs)
            {
                totalChecks++;
                if (providedArgs.Contains(required))
                {
                    passedChecks++;
                }
                else
                {
                    issues.Add($"{call.Name}: missing '{required}'");
                }
            }
        }
        
        if (totalChecks == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No argument requirements to check."));
        }
        
        var score = (double)passedChecks / totalChecks * 100;
        
        var metadata = new Dictionary<string, object>
        {
            ["totalChecks"] = totalChecks,
            ["passedChecks"] = passedChecks,
            ["issues"] = issues
        };
        
        if (score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return Task.FromResult(MetricResult.Pass(Name, score, 
                issues.Any() ? $"Arguments mostly valid. Issues: {issues.Count}" : "All required arguments provided.",
                metadata));
        }
        else
        {
            return Task.FromResult(MetricResult.Fail(Name, 
                $"Missing arguments: {string.Join("; ", issues.Take(5))}", score, metadata));
        }
    }
}

/// <summary>
/// Measures whether tools executed successfully without errors.
/// </summary>
public class ToolSuccessMetric : IAgenticMetric
{
    public string Name => "code_tool_success";
    public string Description => "Measures the success rate of tool executions.";
    public bool RequiresToolUsage => true;
    
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (context.ToolUsage == null || context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tools called."));
        }
        
        var totalCalls = context.ToolUsage.Count;
        var successfulCalls = context.ToolUsage.Calls.Count(c => !c.HasError);
        var failedCalls = context.ToolUsage.Calls.Where(c => c.HasError).ToList();
        
        var score = (double)successfulCalls / totalCalls * 100;
        
        var metadata = new Dictionary<string, object>
        {
            ["totalCalls"] = totalCalls,
            ["successfulCalls"] = successfulCalls,
            ["failedCalls"] = failedCalls.Select(c => new { c.Name, Error = c.Exception?.Message }).ToList()
        };
        
        if (score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return Task.FromResult(MetricResult.Pass(Name, score,
                $"{successfulCalls}/{totalCalls} tools executed successfully.", metadata));
        }
        else
        {
            var errors = string.Join("; ", failedCalls.Take(3).Select(c => $"{c.Name}: {c.Exception?.Message}"));
            return Task.FromResult(MetricResult.Fail(Name,
                $"Tool failures: {errors}", score, metadata));
        }
    }
}

/// <summary>
/// Measures whether the agent completed the task effectively using AI evaluation.
/// </summary>
public class TaskCompletionMetric : IAgenticMetric
{
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<string> _completionCriteria;
    
    public string Name => "llm_task_completion";
    public string Description => "Measures whether the agent effectively completed the requested task.";
    public bool RequiresToolUsage => false;
    
    /// <summary>
    /// Create a task completion metric.
    /// </summary>
    /// <param name="chatClient">Chat client for AI evaluation.</param>
    /// <param name="completionCriteria">Criteria for determining task completion.</param>
    public TaskCompletionMetric(IChatClient chatClient, IEnumerable<string>? completionCriteria = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _completionCriteria = completionCriteria?.ToList() ?? new List<string>
        {
            "The response addresses the user's request",
            "The output is complete and actionable",
            "No steps were missed"
        };
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var criteriaList = string.Join("\n", _completionCriteria.Select((c, i) => $"{i + 1}. {c}"));
        var toolsUsed = context.ToolUsage != null 
            ? $"TOOLS USED: {string.Join(", ", context.ToolUsage.Calls.Select(c => c.Name))}" 
            : "";
        
        var prompt = BuildTaskCompletionPrompt(context.Input, context.Output, toolsUsed, criteriaList);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParseCompletionResult(response.Text ?? "");
    }
    
    private static string BuildTaskCompletionPrompt(string input, string output, string toolsUsed, string criteriaList)
    {
        var toolsPart = string.IsNullOrEmpty(toolsUsed) ? "" : "\n" + toolsUsed + "\n";
        
        return """
            You are an expert evaluator assessing task completion.
            
            USER REQUEST:
            """ + input + """
            
            AGENT RESPONSE:
            """ + output + toolsPart + """
            
            COMPLETION CRITERIA:
            """ + criteriaList + """
            
            Evaluate whether the agent successfully completed the task.
            
            Respond with a JSON object:
            {
                "score": <0-100 completion score>,
                "criteriaResults": [
                    {"criterion": "<criterion text>", "met": true/false, "reason": "<why>"}
                ],
                "reasoning": "<overall explanation>"
            }
            """;
    }
    
    private MetricResult ParseCompletionResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate task completion - failed to parse response.");
        }
        
        var metadata = new Dictionary<string, object> { ["reasoning"] = parsed.Reasoning ?? "" };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"Task completed: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Task incomplete: {parsed.Reasoning}", parsed.Score, metadata);
        }
    }
}

/// <summary>
/// Measures the efficiency of tool usage (number of calls, retries, etc.).
/// </summary>
public class ToolEfficiencyMetric : IAgenticMetric
{
    private readonly int _maxExpectedCalls;
    private readonly TimeSpan _maxExpectedDuration;
    
    public string Name => "code_tool_efficiency";
    public string Description => "Measures the efficiency of tool usage in terms of call count and duration.";
    public bool RequiresToolUsage => true;
    
    /// <summary>
    /// Create a tool efficiency metric.
    /// </summary>
    /// <param name="maxExpectedCalls">Maximum expected number of tool calls. Defaults to <see cref="EvaluationDefaults.DefaultMaxExpectedToolCalls"/>.</param>
    /// <param name="maxExpectedDuration">Maximum expected total tool duration. Defaults to <see cref="EvaluationDefaults.DefaultMaxToolDuration"/>.</param>
    public ToolEfficiencyMetric(int? maxExpectedCalls = null, TimeSpan? maxExpectedDuration = null)
    {
        _maxExpectedCalls = maxExpectedCalls ?? EvaluationDefaults.DefaultMaxExpectedToolCalls;
        _maxExpectedDuration = maxExpectedDuration ?? EvaluationDefaults.DefaultMaxToolDuration;
    }
    
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (context.ToolUsage == null || context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tools called - maximally efficient."));
        }
        
        var callCount = context.ToolUsage.Count;
        var totalDuration = context.ToolUsage.TotalToolTime;
        
        // Score based on call count (100 if within expected, decreasing after)
        var callScore = callCount <= _maxExpectedCalls 
            ? 100 
            : Math.Max(0, 100 - (callCount - _maxExpectedCalls) * 10);
        
        // Score based on duration
        var durationScore = totalDuration <= _maxExpectedDuration
            ? 100
            : Math.Max(0, 100 - (totalDuration.TotalSeconds - _maxExpectedDuration.TotalSeconds) * 5);
        
        // Check for repeated calls (potential retries)
        var duplicateCalls = context.ToolUsage.Calls
            .GroupBy(c => c.Name)
            .Where(g => g.Count() > 1)
            .Select(g => new { Tool = g.Key, Count = g.Count() })
            .ToList();
        
        var duplicatePenalty = duplicateCalls.Sum(d => (d.Count - 1) * EvaluationDefaults.DuplicateToolPenaltyPercent);
        
        var finalScore = Math.Max(0, (callScore + durationScore) / 2 - duplicatePenalty);
        
        var metadata = new Dictionary<string, object>
        {
            ["callCount"] = callCount,
            ["maxExpectedCalls"] = _maxExpectedCalls,
            ["totalDuration"] = totalDuration.ToString(),
            ["maxExpectedDuration"] = _maxExpectedDuration.ToString(),
            ["duplicateCalls"] = duplicateCalls
        };
        
        if (finalScore >= EvaluationDefaults.PassingScoreThreshold)
        {
            return Task.FromResult(MetricResult.Pass(Name, finalScore,
                $"Efficient: {callCount} calls in {totalDuration.TotalSeconds:F1}s", metadata));
        }
        else
        {
            var issues = new List<string>();
            if (callCount > _maxExpectedCalls) issues.Add($"{callCount} calls (expected ≤{_maxExpectedCalls})");
            if (totalDuration > _maxExpectedDuration) issues.Add($"Duration {totalDuration.TotalSeconds:F1}s (expected ≤{_maxExpectedDuration.TotalSeconds}s)");
            if (duplicateCalls.Any()) issues.Add($"Repeated calls: {string.Join(", ", duplicateCalls.Select(d => $"{d.Tool}x{d.Count}"))}");
            
            return Task.FromResult(MetricResult.Fail(Name,
                $"Efficiency issues: {string.Join("; ", issues)}", finalScore, metadata));
        }
    }
}
