// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ToolSelectionMetric - a deterministic agentic metric.
/// </summary>
public class ToolSelectionMetricTests
{
    [Fact]
    public async Task EvaluateAsync_AllExpectedToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather", "FormatResponse" });
        var toolUsage = CreateToolUsage("GetWeather", "FormatResponse");
        
        var context = new EvaluationContext
        {
            Input = "What's the weather?",
            Output = "It's sunny.",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("code_tool_selection", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsCalled_WhenExpected_ReturnsFail()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather" });
        
        var context = new EvaluationContext
        {
            Input = "What's the weather?",
            Output = "I don't know.",
            ToolUsage = null // No tools called
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("GetWeather", result.Explanation);
        Assert.Contains("none were called", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsExpected_NoToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolSelectionMetric(Array.Empty<string>());
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi there!",
            ToolUsage = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_PartialMatch_ReturnsProportionalScore()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "ToolA", "ToolB", "ToolC" });
        var toolUsage = CreateToolUsage("ToolA", "ToolB"); // Missing ToolC
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 2/3 matched = ~66.67%
        Assert.True(result.Score < 70); // Should fail threshold
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_ExtraToolsCalled_ReducesScore()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "ToolA" });
        var toolUsage = CreateToolUsage("ToolA", "ExtraTool1", "ExtraTool2");
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 100% match but penalized for extra tools
        Assert.True(result.Score < 100);
    }
    
    [Fact]
    public async Task EvaluateAsync_CaseInsensitiveMatching()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather", "SendEmail" });
        var toolUsage = CreateToolUsage("getweather", "SENDEMAIL"); // Different case
        
        var context = new EvaluationContext
        {
            Input = "Check weather and send email",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_EmptyToolUsage_WhenExpected_ReturnsFail()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "RequiredTool" });
        var toolUsage = new ToolUsageReport(); // Empty, no calls
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
    }
    
    [Fact]
    public void Constructor_NullExpectedTools_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ToolSelectionMetric(null!));
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "Tool" });
        
        // Assert
        Assert.Equal("code_tool_selection", metric.Name);
        Assert.True(metric.RequiresToolUsage);
    }
    
    // ── BUG-30: strict-order = ordered subsequence, independent of completeness ──

    [Fact]
    public async Task EvaluateAsync_StrictOrder_InterleavedExtraAtFront_NoOrderPenalty()
    {
        // Expected A,B in order; an allowed extra tool runs first. Relative order of A,B is correct,
        // so there must be NO order penalty (only the extra-tool penalty). Before BUG-30 the
        // positional compare flagged the leading extra as an order violation (over-strict).
        var metric = new ToolSelectionMetric(new[] { "ToolA", "ToolB" }, strictOrder: true);
        var context = new EvaluationContext
        {
            Input = "x", Output = "y",
            ToolUsage = CreateToolUsage("ExtraTool", "ToolA", "ToolB")
        };

        var result = await metric.EvaluateAsync(context);

        // 100 (2/2 matched) - 10 (one extra) - 0 (order OK) = 90.
        Assert.Equal(90, result.Score);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_StrictOrder_MissingToolButPresentOnesOutOfOrder_AppliesOrderPenalty()
    {
        // ToolC missing AND the present tools (A,B) are swapped. Strict ordering must still be
        // evaluated despite the missing tool (previously a missing tool skipped ordering entirely,
        // under-applying the penalty).
        var strict = new ToolSelectionMetric(new[] { "ToolA", "ToolB", "ToolC" }, strictOrder: true);
        var lenient = new ToolSelectionMetric(new[] { "ToolA", "ToolB", "ToolC" }, strictOrder: false);
        var ctx = new EvaluationContext { Input = "x", Output = "y", ToolUsage = CreateToolUsage("ToolB", "ToolA") };

        var strictResult = await strict.EvaluateAsync(ctx);
        var lenientResult = await lenient.EvaluateAsync(ctx);

        // Both score 2/3 ≈ 66.67 for matching; strict subtracts the 20-pt order penalty.
        Assert.True(strictResult.Score < lenientResult.Score,
            $"strict ({strictResult.Score}) should be penalized for order vs lenient ({lenientResult.Score})");
        Assert.Equal(lenientResult.Score - 20, strictResult.Score, 4);
    }

    [Fact]
    public async Task EvaluateAsync_StrictOrder_AllPresentButOutOfOrder_AppliesOrderPenalty()
    {
        // Regression guard: a genuine out-of-order (B before A) is still penalized.
        var metric = new ToolSelectionMetric(new[] { "ToolA", "ToolB" }, strictOrder: true);
        var context = new EvaluationContext
        {
            Input = "x", Output = "y",
            ToolUsage = CreateToolUsage("ToolB", "ToolA")
        };

        var result = await metric.EvaluateAsync(context);

        // 100 - 0 extra - 20 order = 80.
        Assert.Equal(80, result.Score);
    }

    // Helper to create tool usage report
    private static ToolUsageReport CreateToolUsage(params string[] toolNames)
    {
        var report = new ToolUsageReport();
        var order = 1;
        foreach (var name in toolNames)
        {
            report.AddCall(new ToolCallRecord
            {
                Name = name,
                CallId = Guid.NewGuid().ToString(),
                Order = order++
            });
        }
        return report;
    }
}
