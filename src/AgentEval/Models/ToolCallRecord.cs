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
