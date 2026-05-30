// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using AgentEval.Assertions;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for ToolUsageAssertions fluent API
/// </summary>
public class ToolUsageAssertionsTests
{
    [Fact]
    public void HaveCalledTool_WhenToolExists_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        
        var exception = Record.Exception(() => report.Should().HaveCalledTool("TestTool"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveCalledTool_WhenToolMissing_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "OtherTool", CallId = "call-1", Order = 1 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledTool("TestTool"));
        
        Assert.Contains("TestTool", exception.Message);
        Assert.Contains("OtherTool", exception.Message);
    }
    
    [Fact]
    public void NotHaveCalledTool_WhenToolMissing_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "OtherTool", CallId = "call-1", Order = 1 });
        
        var exception = Record.Exception(() => report.Should().NotHaveCalledTool("TestTool"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void NotHaveCalledTool_WhenToolExists_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "TestTool", CallId = "call-1", Order = 1 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().NotHaveCalledTool("TestTool"));
        
        Assert.Contains("NOT to be called", exception.Message);
    }
    
    [Fact]
    public void HaveCallCount_WithCorrectCount_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Tool2", CallId = "call-2", Order = 2 });
        
        var exception = Record.Exception(() => report.Should().HaveCallCount(2));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveCallCount_WithWrongCount_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCallCount(2));
        
        Assert.Contains("Expected 2", exception.Message);
        Assert.Contains("1 call(s)", exception.Message);
    }
    
    [Fact]
    public void HaveNoErrors_WithNoErrors_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1, Result = "Success" });
        
        var exception = Record.Exception(() => report.Should().HaveNoErrors());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveNoErrors_WithErrors_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord 
        { 
            Name = "Tool1", 
            CallId = "call-1",
            Order = 1, 
            Exception = new InvalidOperationException("Test error") 
        });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveNoErrors());
        
        Assert.Contains("error", exception.Message);
        Assert.Contains("Tool1", exception.Message);
    }
    
    [Fact]
    public void HaveCallOrder_WithCorrectOrder_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-2", Order = 2 });
        report.AddCall(new ToolCallRecord { Name = "Third", CallId = "call-3", Order = 3 });
        
        var exception = Record.Exception(() => 
            report.Should().HaveCallOrder("First", "Second", "Third"));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveCallOrder_WithWrongOrder_ThrowsException()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Second", CallId = "call-1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "First", CallId = "call-2", Order = 2 });
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCallOrder("First", "Second"));
        
        Assert.Contains("order", exception.Message.ToLower());
    }
    
    [Fact]
    public void HaveCallOrder_RepeatedTool_AllOccurrencesInOrder_DoesNotThrow()
    {
        // BUG-14: ["Search","Book","Search"] must match two DISTINCT Search calls via a cursor,
        // not resolve both to the first Search.
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Search", CallId = "c1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Book", CallId = "c2", Order = 2 });
        report.AddCall(new ToolCallRecord { Name = "Search", CallId = "c3", Order = 3 });

        var exception = Record.Exception(() =>
            report.Should().HaveCallOrder("Search", "Book", "Search"));

        Assert.Null(exception);
    }

    [Fact]
    public void HaveCallOrder_RepeatedTool_SecondOccurrenceMissing_Throws()
    {
        // Only one Search was called, so the second expected "Search" cannot be matched after Book.
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Search", CallId = "c1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Book", CallId = "c2", Order = 2 });

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveCallOrder("Search", "Book", "Search"));

        Assert.Contains("Search", exception.Message);
    }

    [Fact]
    public void HaveCallOrder_RepeatedTool_OnlyFirstOccurrence_DoesNotFalselyPass()
    {
        // "A then B then A again" must require a real second A AFTER B; B,A,A (no A after the
        // matched first A-before-B) — here A,B with a trailing A satisfies; A,A,B must FAIL the
        // final "...,Book" because no Book occurs after the second Search.
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Search", CallId = "c1", Order = 1 });
        report.AddCall(new ToolCallRecord { Name = "Search", CallId = "c2", Order = 2 });

        var exception = Assert.Throws<ToolAssertionException>(() =>
            report.Should().HaveCallOrder("Search", "Book"));

        Assert.Contains("Book", exception.Message);
    }

    [Fact]
    public void HaveCalledAnyTool_WithCalls_DoesNotThrow()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "Tool1", CallId = "call-1", Order = 1 });
        
        var exception = Record.Exception(() => report.Should().HaveCalledAnyTool());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveCalledAnyTool_WithNoCalls_ThrowsException()
    {
        var report = new ToolUsageReport();
        
        var exception = Assert.Throws<ToolAssertionException>(() => 
            report.Should().HaveCalledAnyTool());
        
        Assert.Contains("at least one tool", exception.Message);
    }
}
