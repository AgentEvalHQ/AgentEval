// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Report of all tool usage during an agent run.
/// </summary>
public class ToolUsageReport
{
    private readonly List<ToolCallRecord> _calls = [];
    
    /// <summary>All tool calls in order of invocation.</summary>
    public IReadOnlyList<ToolCallRecord> Calls => _calls;
    
    /// <summary>Number of tool calls made.</summary>
    public int Count => _calls.Count;
    
    /// <summary>Names of all tools called (in order, may have duplicates).</summary>
    public IEnumerable<string> ToolNames => _calls.Select(c => c.Name);
    
    /// <summary>Unique tool names called.</summary>
    public IEnumerable<string> UniqueToolNames => _calls.Select(c => c.Name).Distinct();
    
    /// <summary>Whether any tool call resulted in an error.</summary>
    public bool HasErrors => _calls.Any(c => c.HasError);
    
    /// <summary>Total time spent in tool execution (for calls with timing).</summary>
    public TimeSpan TotalToolTime => TimeSpan.FromTicks(
        _calls.Where(c => c.HasTiming).Sum(c => c.Duration!.Value.Ticks));
    
    /// <summary>Add a tool call to the report.</summary>
    public void AddCall(ToolCallRecord call) => _calls.Add(call);
    
    /// <summary>Get all calls to a specific tool (case-insensitive).</summary>
    public IEnumerable<ToolCallRecord> GetCallsByName(string toolName) =>
        _calls.Where(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>Check if a tool was called (case-insensitive).</summary>
    public bool WasToolCalled(string toolName) =>
        _calls.Any(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>Get the order position of a tool's first call (1-based, 0 if not called).</summary>
    public int GetToolOrder(string toolName) =>
        _calls.FirstOrDefault(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))?.Order ?? 0;
    
    public override string ToString()
    {
        if (Count == 0)
            return "No tools called";
        return $"{Count} tool(s): {string.Join(" → ", ToolNames)}";
    }
}
