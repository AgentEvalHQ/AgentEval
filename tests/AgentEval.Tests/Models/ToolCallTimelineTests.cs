// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

public class ToolCallTimelineTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var timeline = ToolCallTimeline.Create("test-123");

        Assert.Equal("test-123", timeline.ConversationId);
        Assert.Equal(0, timeline.TotalToolCalls);
        Assert.Equal(TimeSpan.Zero, timeline.TotalDuration);
        Assert.Empty(timeline.Invocations);
    }

    [Fact]
    public void AddInvocation_TracksSequenceIndex()
    {
        var timeline = ToolCallTimeline.Create();

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "tool_a",
            StartTime = TimeSpan.FromMilliseconds(0),
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = true
        });

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "tool_b",
            StartTime = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(200),
            Succeeded = true
        });

        Assert.Equal(2, timeline.TotalToolCalls);
        Assert.Equal(0, timeline.Invocations[0].SequenceIndex);
        Assert.Equal(1, timeline.Invocations[1].SequenceIndex);
    }

    [Fact]
    public void Statistics_CalculateCorrectly()
    {
        var timeline = ToolCallTimeline.Create();
        timeline.TotalDuration = TimeSpan.FromMilliseconds(500);

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "search",
            StartTime = TimeSpan.FromMilliseconds(0),
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = true
        });

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "search",
            StartTime = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(50),
            Succeeded = true
        });

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "calculator",
            StartTime = TimeSpan.FromMilliseconds(150),
            Duration = TimeSpan.FromMilliseconds(200),
            Succeeded = false,
            ErrorMessage = "Division by zero"
        });

        Assert.Equal(3, timeline.TotalToolCalls);
        Assert.Equal(2, timeline.SuccessfulToolCalls);
        Assert.Equal(1, timeline.FailedToolCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(350), timeline.TotalToolTime);
        Assert.Equal(70, timeline.ToolTimePercentage, 0.1);

        var stats = timeline.GetToolStatistics();
        Assert.Equal(2, stats["search"].CallCount);
        Assert.Equal(2, stats["search"].SuccessCount);
        Assert.Equal(1, stats["calculator"].FailureCount);
    }

    [Fact]
    public void SlowestTool_ReturnsCorrectly()
    {
        var timeline = ToolCallTimeline.Create();

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "fast",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(50),
            Succeeded = true
        });

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "slow",
            StartTime = TimeSpan.FromMilliseconds(50),
            Duration = TimeSpan.FromMilliseconds(500),
            Succeeded = true
        });

        var slowest = timeline.SlowestTool;
        Assert.NotNull(slowest);
        Assert.Equal("slow", slowest.ToolName);
    }

    [Fact]
    public void ToAsciiDiagram_GeneratesOutput()
    {
        var timeline = ToolCallTimeline.Create();
        timeline.TotalDuration = TimeSpan.FromMilliseconds(1000);
        timeline.TimeToFirstToken = TimeSpan.FromMilliseconds(100);

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "search",
            StartTime = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(300),
            Succeeded = true
        });

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "calculator",
            StartTime = TimeSpan.FromMilliseconds(400),
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = false,
            ErrorMessage = "Error occurred"
        });

        var diagram = timeline.ToAsciiDiagram();

        Assert.Contains("Tool Call Timeline", diagram);
        Assert.Contains("search", diagram);
        Assert.Contains("calculator", diagram);
        Assert.Contains("✓", diagram);
        Assert.Contains("✗", diagram);
        Assert.Contains("Error", diagram);
    }

    [Fact]
    public void ToCompactSummary_ReturnsOneLineSummary()
    {
        var timeline = ToolCallTimeline.Create();
        timeline.TotalDuration = TimeSpan.FromMilliseconds(500);
        timeline.TimeToFirstToken = TimeSpan.FromMilliseconds(50);

        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "test",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = false,
            ErrorMessage = "Test error"
        });

        var summary = timeline.ToCompactSummary();

        Assert.Contains("1 tools", summary);
        Assert.Contains("500ms", summary);
        Assert.Contains("50ms", summary);
        Assert.Contains("1 failed", summary);
    }

    [Fact]
    public void ToolInvocation_EndTime_CalculatesCorrectly()
    {
        var invocation = new ToolInvocation
        {
            ToolName = "test",
            StartTime = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(50),
            Succeeded = true
        };

        Assert.Equal(TimeSpan.FromMilliseconds(150), invocation.EndTime);
    }
}
