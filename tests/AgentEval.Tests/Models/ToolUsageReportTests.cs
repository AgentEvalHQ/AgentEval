// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for ToolUsageReport
/// </summary>
public class ToolUsageReportTests
{
    [Fact]
    public void Count_WithNoCalls_ReturnsZero()
    {
        var report = new ToolUsageReport();
        
        Assert.Equal(0, report.Count);
        Assert.False(report.HasErrors);
    }
    
    [Fact]
    public void Count_WithCalls_ReturnsCorrectCount()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Tool2", CallId = "call-2", Order = 2 });
        
        Assert.Equal(2, report.Count);
    }
    
    [Fact]
    public void WasToolCalled_WithExistingTool_ReturnsTrue()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        
        Assert.True(report.WasToolCalled("TestTool"));
        Assert.True(report.WasToolCalled("testtool")); // case-insensitive
    }
    
    [Fact]
    public void WasToolCalled_WithNonExistingTool_ReturnsFalse()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        
        Assert.False(report.WasToolCalled("OtherTool"));
    }
    
    [Fact]
    public void GetToolOrder_ReturnsCorrectOrder()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "FirstTool", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "SecondTool", CallId = "call-2", Order = 2 });
        
        Assert.Equal(1, report.GetToolOrder("FirstTool"));
        Assert.Equal(2, report.GetToolOrder("SecondTool"));
        Assert.Equal(0, report.GetToolOrder("NonExistent"));
    }
    
    [Fact]
    public void UniqueToolNames_WithDuplicates_ReturnsUnique()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Tool2", CallId = "call-2", Order = 2 });
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-3", Order = 3 });
        
        var unique = report.UniqueToolNames.ToList();
        
        Assert.Equal(2, unique.Count);
        Assert.Contains("Tool1", unique);
        Assert.Contains("Tool2", unique);
    }
    
    [Fact]
    public void HasErrors_WithError_ReturnsTrue()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "Tool1", 
            CallId = "call-1",
            Order = 1,
            Exception = new InvalidOperationException("Error")
        });
        
        Assert.True(report.HasErrors);
    }
    
    [Fact]
    public void GetCallsByName_ReturnsAllMatching()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Tool2", CallId = "call-2", Order = 2 });
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-3", Order = 3 });
        
        var calls = report.GetCallsByName("Tool1").ToList();
        
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("Tool1", c.Name));
    }
    
    [Fact]
    public void ToString_WithCalls_ReturnsFormattedString()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Tool2", CallId = "call-2", Order = 2 });
        
        var str = report.ToString();
        
        Assert.Contains("2 tool(s)", str);
        Assert.Contains("Tool1", str);
        Assert.Contains("Tool2", str);
    }
    
    [Fact]
    public void ToString_WithNoCalls_ReturnsNoToolsCalled()
    {
        var report = new ToolUsageReport();
        
        Assert.Equal("No tools called", report.ToString());
    }
}
