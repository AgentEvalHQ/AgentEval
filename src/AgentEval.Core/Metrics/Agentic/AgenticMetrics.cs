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
        if (context.ToolUsage is null)
            return ToolUsageNotRecorded.Result(Name);

        if (context.ToolUsage.Count == 0)
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
        
        // Strict-order penalty (BUG-30): the expected tools that were actually called must appear in
        // calledTools in their expected relative order — i.e. as an ordered subsequence. This is
        // checked INDEPENDENTLY of completeness (a missing tool no longer skips ordering, which
        // previously under-applied the penalty) and is tolerant of interleaved extra tools (an
        // allowed extra at the front is no longer a false order violation, which was over-strict).
        if (_strictOrder)
        {
            var expectedPresent = expectedLower.Where(calledTools.Contains).ToList();
            if (!IsOrderedSubsequence(expectedPresent, calledTools))
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

    /// <summary>
    /// Returns <see langword="true"/> when every element of <paramref name="needle"/> appears in
    /// <paramref name="haystack"/> in the same relative order (an ordered subsequence); interleaved
    /// elements in <paramref name="haystack"/> are ignored. Empty <paramref name="needle"/> matches
    /// vacuously.
    /// </summary>
    private static bool IsOrderedSubsequence(IReadOnlyList<string> needle, IReadOnlyList<string> haystack)
    {
        var n = 0;
        foreach (var item in haystack)
        {
            if (n < needle.Count && item == needle[n])
                n++;
        }
        return n == needle.Count;
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
        if (context.ToolUsage is null)
            return ToolUsageNotRecorded.Result(Name);

        // A vacuous check is not a pass (owner's decision, 2026-10-21): with no call, or no call to a tool that has
        // argument requirements, nothing was checked. It read PASS 100. Not measured: no argument was checked.
        if (context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.NotMeasured(Name, NotMeasuredCause.NotApplicable,
                "The run's tool calls were recorded and no tool was called, so no argument was checked: tool arguments " +
                "were not measured (not applicable to this run)."));
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
            return Task.FromResult(MetricResult.NotMeasured(Name, NotMeasuredCause.NotApplicable,
                $"None of the {context.ToolUsage.Count} recorded call(s) is to a tool with argument requirements, so no " +
                "argument was checked: tool arguments were not measured (not applicable to this run)."));
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
        if (context.ToolUsage is null)
            return ToolUsageNotRecorded.Result(Name);

        if (context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tools called."));
        }

        // A success is a call that RAN without error. A call with no paired result was only emitted (a request the run
        // never executed, an approval request, a rejected approval), so it is not a success either. Reading HasError
        // alone counted every such call as one (T58).
        var totalCalls = context.ToolUsage.Count;
        var successfulCalls = context.ToolUsage.Calls.Count(c => c.WasExecuted && !c.HasError);
        var failedCalls = context.ToolUsage.Calls.Where(c => c.HasError).ToList();
        var notExecutedCalls = context.ToolUsage.Calls.Where(c => !c.WasExecuted && !c.HasError).ToList();

        var score = (double)successfulCalls / totalCalls * 100;

        var metadata = new Dictionary<string, object>
        {
            ["totalCalls"] = totalCalls,
            ["successfulCalls"] = successfulCalls,
            ["failedCalls"] = failedCalls.Select(c => new { c.Name, Error = c.Exception?.Message }).ToList(),
            ["notExecutedCalls"] = notExecutedCalls.Select(c => c.Name).ToList()
        };
        var notRun = notExecutedCalls.Count == 0
            ? ""
            : $" {notExecutedCalls.Count} emitted but never ran (no execution was recorded): {string.Join(", ", notExecutedCalls.Select(c => c.Name))}.";

        if (score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return Task.FromResult(MetricResult.Pass(Name, score,
                $"{successfulCalls}/{totalCalls} tools executed successfully.{notRun}", metadata));
        }
        else
        {
            var errors = string.Join("; ", failedCalls.Take(3).Select(c => $"{c.Name}: {c.Exception?.Message}"));
            var failures = failedCalls.Count == 0
                ? $"{successfulCalls}/{totalCalls} tools executed successfully."
                : $"Tool failures: {errors}.";
            return Task.FromResult(MetricResult.Fail(Name,
                $"{failures}{notRun}", score, metadata));
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
        
        // A failed judge call or an unreadable reply is not measured, never a Fail at 0.
        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Task completion", cancellationToken);
        return JudgeReplyEvidence.Model(failed ?? ParseCompletionResult(response!), BuildTaskCompletionPrompt("", "", string.IsNullOrEmpty(toolsUsed) ? "" : "{tools}", criteriaList));
    }
    
    private static string BuildTaskCompletionPrompt(string input, string output, string toolsUsed, string criteriaList)
    {
        var toolsPart = string.IsNullOrEmpty(toolsUsed) ? "" : "\n" + toolsUsed + "\n";
        
        return """
            You are an expert evaluator assessing task completion.
            
            USER REQUEST:
            """ + input + """
            
            AGENT RESPONSE:
            <agent_response>
            """ + output + toolsPart + """
            </agent_response>

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
    
    private MetricResult ParseCompletionResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Task completion", response, failure);
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
        if (context.ToolUsage is null)
            return ToolUsageNotRecorded.Result(Name);

        if (context.ToolUsage.Count == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100, "No tools called - maximally efficient."));
        }

        var callCount = context.ToolUsage.Count;
        var totalDuration = context.ToolUsage.TotalToolTime;

        // Score based on call count (100 if within expected, decreasing after)
        var callScore = callCount <= _maxExpectedCalls
            ? 100
            : Math.Max(0, 100 - (callCount - _maxExpectedCalls) * 10);

        // Score based on duration, only when every call was timed. The non-streaming path records no timing (unless a
        // Glass Box trace back-fills it) and TotalToolTime sums the timed calls only, so an untimed run read a total of
        // 0s and a perfect duration score: half of this metric credited for something nobody measured (T58).
        var untimedCalls = context.ToolUsage.Calls.Count(c => !c.HasTiming);
        double? durationScore = null;
        if (untimedCalls == 0)
        {
            durationScore = totalDuration <= _maxExpectedDuration
                ? 100
                : Math.Max(0, 100 - (totalDuration.TotalSeconds - _maxExpectedDuration.TotalSeconds) * 5);
        }

        // Check for repeated calls (potential retries)
        var duplicateCalls = context.ToolUsage.Calls
            .GroupBy(c => c.Name)
            .Where(g => g.Count() > 1)
            .Select(g => new { Tool = g.Key, Count = g.Count() })
            .ToList();
        
        var duplicatePenalty = duplicateCalls.Sum(d => (d.Count - 1) * EvaluationDefaults.DuplicateToolPenaltyPercent);
        
        // Without a duration the score rests on the call count alone, and the explanation says so.
        var finalScore = Math.Max(0, (durationScore is { } timed ? (callScore + timed) / 2 : callScore) - duplicatePenalty);
        var durationNote = durationScore is null
            ? $" (duration not measured: {untimedCalls} of {callCount} calls carry no timing)"
            : "";

        var metadata = new Dictionary<string, object>
        {
            ["callCount"] = callCount,
            ["maxExpectedCalls"] = _maxExpectedCalls,
            ["totalDuration"] = totalDuration.ToString(),
            ["maxExpectedDuration"] = _maxExpectedDuration.ToString(),
            ["durationMeasured"] = durationScore is not null,
            ["duplicateCalls"] = duplicateCalls
        };

        if (finalScore >= EvaluationDefaults.PassingScoreThreshold)
        {
            return Task.FromResult(MetricResult.Pass(Name, finalScore,
                durationScore is null
                    ? $"Efficient: {callCount} calls{durationNote}"
                    : $"Efficient: {callCount} calls in {totalDuration.TotalSeconds:F1}s", metadata));
        }
        else
        {
            var issues = new List<string>();
            if (callCount > _maxExpectedCalls) issues.Add($"{callCount} calls (expected ≤{_maxExpectedCalls})");
            if (durationScore is not null && totalDuration > _maxExpectedDuration) issues.Add($"Duration {totalDuration.TotalSeconds:F1}s (expected ≤{_maxExpectedDuration.TotalSeconds}s)");
            if (duplicateCalls.Any()) issues.Add($"Repeated calls: {string.Join(", ", duplicateCalls.Select(d => $"{d.Tool}x{d.Count}"))}");

            return Task.FromResult(MetricResult.Fail(Name,
                $"Efficiency issues: {string.Join("; ", issues)}{durationNote}", finalScore, metadata));
        }
    }
}

/// <summary>
/// The result a tool metric returns when no tool data was recorded for the run.
/// </summary>
/// <remarks>
/// <see cref="EvaluationContext.ToolUsage"/> is <see langword="null"/> when nothing recorded the run's tool calls: tool
/// tracking off, an adapter that returned no messages, a run that threw before its calls were read, a dataset row with
/// no trace. That is not a run with no tool calls, which is an empty report and a real result. The tool metrics read the
/// null as "no tools called" and scored it 100 (arguments, success, efficiency, skill disclosure) or a measured 0
/// (selection) (T58).
/// </remarks>
internal static class ToolUsageNotRecorded
{
    internal const string Reason =
        "No tool data was recorded for this run (tool tracking was off, the adapter returned no messages, or the run " +
        "failed before its tool calls were read), so tool use was not measured. A recorded run with no tool calls is a " +
        "real result; this is not one.";

    internal static Task<MetricResult> Result(string metricName) =>
        Task.FromResult(MetricResult.NotMeasured(metricName, NotMeasuredCause.MissingInput, Reason));
}
