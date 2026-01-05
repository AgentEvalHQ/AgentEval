// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using System.Text.Json;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for ToolCallRecord
/// </summary>
public class ToolCallRecordTests
{
    [Fact]
    public void GetArgumentsAsJson_WithNoArguments_ReturnsEmptyObject()
    {
        var record = new ToolCallRecord { Name = "TestTool", CallId = "call-1" };
        
        var json = record.GetArgumentsAsJson();
        
        Assert.Equal("{}", json);
    }
    
    [Fact]
    public void GetArgumentsAsJson_WithArguments_ReturnsValidJson()
    {
        var record = new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Arguments = new Dictionary<string, object?>
            {
                ["name"] = "test",
                ["count"] = 42
            }
        };
        
        var json = record.GetArgumentsAsJson();
        
        Assert.Contains("\"name\"", json);
        Assert.Contains("test", json);
        Assert.Contains("42", json);
    }
    
    [Fact]
    public void GetArgument_WithValidArgument_ReturnsValue()
    {
        var record = new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Arguments = new Dictionary<string, object?>
            {
                ["name"] = "test"
            }
        };
        
        var value = record.GetArgument<string>("name");
        
        Assert.Equal("test", value);
    }
    
    [Fact]
    public void GetArgument_WithMissingArgument_ReturnsDefault()
    {
        var record = new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Arguments = new Dictionary<string, object?>()
        };
        
        var value = record.GetArgument<string>("missing");
        
        Assert.Null(value);
    }
    
    [Fact]
    public void HasError_WithException_ReturnsTrue()
    {
        var record = new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Exception = new InvalidOperationException("Test error")
        };
        
        Assert.True(record.HasError);
    }
    
    [Fact]
    public void HasError_WithoutException_ReturnsFalse()
    {
        var record = new ToolCallRecord { Name = "TestTool", CallId = "call-1" };
        
        Assert.False(record.HasError);
    }
    
    [Fact]
    public void Duration_WithStartAndEnd_ReturnsCorrectDuration()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddMilliseconds(500);
        
        var record = new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            StartTime = start,
            EndTime = end
        };
        
        Assert.True(record.HasTiming);
        Assert.NotNull(record.Duration);
        Assert.Equal(500, record.Duration.Value.TotalMilliseconds, precision: 1);
    }
    
    [Fact]
    public void Duration_WithoutTiming_ReturnsNull()
    {
        var record = new ToolCallRecord { Name = "TestTool", CallId = "call-1" };
        
        Assert.False(record.HasTiming);
        Assert.Null(record.Duration);
    }
}
