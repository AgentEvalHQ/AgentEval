// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using AgentEval.Assertions;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for ToolCallAssertion fluent API
/// </summary>
public class ToolCallAssertionTests
{
    [Fact]
    public void BeforeTool_WhenCalledBefore_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2 });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("First").BeforeTool("Second"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void BeforeTool_WhenCalledAfter_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("Second").BeforeTool("First"));
        
        Assert.Contains("before", exception.Message);
    }
    
    [Fact]
    public void AfterTool_WhenCalledAfter_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2 });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("Second").AfterTool("First"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void AfterTool_WhenCalledBefore_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("First").AfterTool("Second"));
        
        Assert.Contains("after", exception.Message);
    }
    
    [Fact]
    public void WithArgument_WhenMatches_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TestTool", 
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["name"] = "value" }
        });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("TestTool").WithArgument("name", "value"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void WithArgument_WhenNotMatches_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TestTool", 
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["name"] = "wrong" }
        });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("TestTool").WithArgument("name", "expected"));
        
        Assert.Contains("expected", exception.Message);
    }
    
    [Fact]
    public void WithArgumentContaining_WhenContains_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TestTool", 
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["query"] = "hello world" }
        });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("TestTool").WithArgumentContaining("query", "world"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void WithResultContaining_WhenContains_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TestTool", 
            CallId = "call-1",
            Order = 1,
            Result = "Success: operation completed"
        });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("TestTool").WithResultContaining("Success"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void WithoutError_WhenNoError_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1, Result = "OK" });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("TestTool").WithoutError());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void WithoutError_WhenHasError_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "TestTool", 
            CallId = "call-1",
            Order = 1, 
            Exception = new InvalidOperationException("Failed") 
        });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("TestTool").WithoutError());
        
        Assert.Contains("without error", exception.Message);
    }
    
    [Fact]
    public void Times_WhenCorrectCount_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-2", Order = 2 });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCalledTool("TestTool").Times(2));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void Times_WhenWrongCount_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("TestTool").Times(3));
        
        Assert.Contains("3 time(s)", exception.Message);
    }
    
    [Fact]
    public void FluentChaining_WorksCorrectly()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1, Result = "OK" });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2, Result = "Done" });
        
        var exception = Record.Exception(() => 
            report.Should()
                .HaveCalledTool("First")
                    .BeforeTool("Second")
                    .WithoutError()
                .And()
                .HaveCallCount(2)
                .HaveNoErrors());
        
        Assert.Null(exception);
    }
}
