// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Models;

/// <summary>
/// Records a single tool/function call made by the agent.
/// </summary>
public class ToolCallRecord
{
    /// <summary>Tool/function name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Unique identifier linking call to result.</summary>
    public required string CallId { get; init; }
    
    /// <summary>Arguments passed to the tool.</summary>
    public IDictionary<string, object?>? Arguments { get; init; }
    
    /// <summary>Result returned by the tool (null if pending or failed).</summary>
    public object? Result { get; set; }

    /// <summary>Exception if tool execution failed.</summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// True iff a paired tool RESULT was observed for this call (i.e. the tool actually executed), as opposed to a
    /// call that was only emitted (intent-to-act). Distinct from <see cref="Result"/> being non-null: a void- or
    /// null-returning tool still executes, so absence of a result value does NOT mean non-execution — only absence
    /// of a paired result does. Set by the extractor when it matches a FunctionResultContent by CallId.
    /// </summary>
    public bool WasExecuted { get; set; }

    /// <summary><see cref="ApprovalState"/> value: the framework asked a human to approve this call and no decision has been observed.</summary>
    public const string ApprovalRequested = "Requested";

    /// <summary><see cref="ApprovalState"/> value: the call was approved. It executed only if a paired result was also observed (<see cref="WasExecuted"/>).</summary>
    public const string ApprovalApproved = "Approved";

    /// <summary><see cref="ApprovalState"/> value: the call was rejected. It never executed; a "failed" result generated for the rejection does not count as one.</summary>
    public const string ApprovalRejected = "Rejected";

    /// <summary>
    /// Where this call stands in a human-approval flow, or <see langword="null"/> when the call was not
    /// approval-gated (the ordinary <c>FunctionCallContent</c> path). One of
    /// <see cref="ApprovalRequested"/>, <see cref="ApprovalApproved"/>, <see cref="ApprovalRejected"/>.
    /// </summary>
    /// <remarks>
    /// Populated only by an approval-aware extraction (ADR-030 Slice 0.5, opt-in:
    /// <c>ToolUsageExtractor.Extract(rawMessages, includeApprovalGatedCalls: true)</c>). MAF answers an
    /// approval-required call with a <c>ToolApprovalRequestContent</c> that <i>wraps</i> the
    /// <c>FunctionCallContent</c>; the default extractor cannot see it and reports the drop instead
    /// (<see cref="ToolUsageReport.DroppedApprovalRequestCount"/>). An approval <i>request</i> is not an
    /// execution — a gated-and-rejected call records as <see cref="WasExecuted"/> <c>false</c> and is still
    /// a call for the purposes of <c>NeverCallTool</c>, because "the agent tried" is what that assertion asks.
    /// </remarks>
    public string? ApprovalState { get; set; }

    /// <summary>Order in which this tool was called (1-based).</summary>
    public int Order { get; init; }
    
    /// <summary>The executor/agent that made this tool call (workflow context).</summary>
    public string? ExecutorId { get; set; }
    
    /// <summary>When tool execution started (streaming only).</summary>
    public DateTimeOffset? StartTime { get; set; }
    
    /// <summary>When tool execution completed (streaming only).</summary>
    public DateTimeOffset? EndTime { get; set; }
    
    /// <summary>Duration of tool execution (streaming only).</summary>
    public TimeSpan? Duration => (StartTime.HasValue && EndTime.HasValue) 
        ? EndTime.Value - StartTime.Value 
        : null;
    
    /// <summary>Whether timing information is available.</summary>
    public bool HasTiming => StartTime.HasValue && EndTime.HasValue;
    
    /// <summary>Whether the tool execution resulted in an error.</summary>
    public bool HasError => Exception != null;
    
    /// <summary>Gets arguments as formatted JSON string for display.</summary>
    public string GetArgumentsAsJson()
    {
        if (Arguments == null || Arguments.Count == 0)
            return "{}";
        return JsonSerializer.Serialize(Arguments, new JsonSerializerOptions { WriteIndented = false });
    }
    
    /// <summary>Gets a specific argument value.</summary>
    public T? GetArgument<T>(string name)
    {
        if (Arguments == null || !Arguments.TryGetValue(name, out var value))
            return default;
        
        if (value is T typed)
            return typed;
        
        if (value is JsonElement element)
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        
        return default;
    }
    
    public override string ToString()
    {
        var args = GetArgumentsAsJson();
        var resultStr = HasError ? $"❌ {Exception?.Message}" : Result?.ToString() ?? "(no result)";
        return $"{Name}({args}) → {resultStr}";
    }
}
