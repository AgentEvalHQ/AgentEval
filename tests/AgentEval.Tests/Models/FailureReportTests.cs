// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

public class FailureReportTests
{
    [Fact]
    public void Create_WithWhyItFailed_SetsProperty()
    {
        var report = new FailureReport
        {
            WhyItFailed = "Test failed because of reason X"
        };

        Assert.Equal("Test failed because of reason X", report.WhyItFailed);
        Assert.Empty(report.Reasons);
        Assert.Empty(report.Suggestions);
    }

    [Fact]
    public void AddReason_FluentChaining_Works()
    {
        var report = new FailureReport { WhyItFailed = "Test failure" }
            .AddReason("Category1", "Description 1")
            .AddReason("Category2", "Description 2", FailureSeverity.Critical);

        Assert.Equal(2, report.Reasons.Count);
        Assert.Equal("Category1", report.Reasons[0].Category);
        Assert.Equal(FailureSeverity.Error, report.Reasons[0].Severity);
        Assert.Equal(FailureSeverity.Critical, report.Reasons[1].Severity);
    }

    [Fact]
    public void AddSuggestion_FluentChaining_Works()
    {
        var report = new FailureReport { WhyItFailed = "Test failure" }
            .AddSuggestion("Fix 1", "Do this to fix it", 0.9)
            .AddSuggestion("Fix 2", "Or try this", 0.5);

        Assert.Equal(2, report.Suggestions.Count);
        // Suggestions should be ordered by confidence
        Assert.Equal("Fix 1", report.Suggestions[0].Title);
        Assert.Equal(0.9, report.Suggestions[0].Confidence);
    }

    [Fact]
    public void PrimaryReason_ReturnsMostSevere()
    {
        var report = new FailureReport { WhyItFailed = "Test failure" }
            .AddReason("Warning", "A warning", FailureSeverity.Warning)
            .AddReason("Error", "An error", FailureSeverity.Error)
            .AddReason("Critical", "A critical error", FailureSeverity.Critical);

        var primary = report.PrimaryReason;

        Assert.NotNull(primary);
        Assert.Equal("Critical", primary.Category);
    }

    [Fact]
    public void IsCritical_ReturnsTrueWhenCriticalReasonExists()
    {
        var report = new FailureReport { WhyItFailed = "Test failure" }
            .AddReason("Error", "An error", FailureSeverity.Error);

        Assert.False(report.IsCritical);

        report.AddReason("Critical", "A critical error", FailureSeverity.Critical);

        Assert.True(report.IsCritical);
    }

    [Fact]
    public void ToFormattedReport_ContainsAllSections()
    {
        var report = new FailureReport
        {
            WhyItFailed = "Tool 'search' failed",
            MetricName = "ToolSuccess",
            Score = 0.0
        }
        .AddReason("ToolError", "Search API returned 500")
        .AddSuggestion("Check API status", "Verify the search API is online");

        var formatted = report.ToFormattedReport(includeTimeline: false);

        Assert.Contains("FAILURE REPORT", formatted);
        Assert.Contains("Tool 'search' failed", formatted);
        Assert.Contains("ToolSuccess", formatted);
        Assert.Contains("Failure Reasons", formatted);
        Assert.Contains("ToolError", formatted);
        Assert.Contains("Suggested Fixes", formatted);
        Assert.Contains("Check API status", formatted);
    }

    [Fact]
    public void ToCompactSummary_ReturnsOneLine()
    {
        var report = new FailureReport
        {
            WhyItFailed = "Evaluation failed",
            MetricName = "Faithfulness"
        }
        .AddReason("ValidationFailed", "Score below threshold");

        var summary = report.ToCompactSummary();

        Assert.DoesNotContain("\n", summary);
        Assert.Contains("FAILED", summary);
        Assert.Contains("Faithfulness", summary);
        Assert.Contains("1 reason", summary);
    }

    [Fact]
    public void FromToolCallFailure_CreatesCorrectReport()
    {
        var failedTool = new ToolInvocation
        {
            ToolName = "search",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = false,
            ErrorMessage = "API timeout"
        };

        var report = FailureReport.FromToolCallFailure(failedTool, "ToolSuccess");

        Assert.Contains("search", report.WhyItFailed);
        Assert.Equal("ToolSuccess", report.MetricName);
        Assert.Equal(0.0, report.Score);
        Assert.Single(report.Reasons);
        Assert.Equal("ToolError", report.Reasons[0].Category);
        Assert.Contains("API timeout", report.Reasons[0].Description);
        Assert.True(report.Suggestions.Count >= 2);
    }

    [Fact]
    public void FromTimeout_CreatesCorrectReport()
    {
        var report = FailureReport.FromTimeout(
            elapsed: TimeSpan.FromSeconds(35),
            limit: TimeSpan.FromSeconds(30),
            metricName: "ResponseTime");

        Assert.Contains("timed out", report.WhyItFailed);
        Assert.Equal("ResponseTime", report.MetricName);
        Assert.True(report.IsCritical);
        Assert.Contains("Timeout", report.Reasons[0].Category);
    }

    [Fact]
    public void FromValidationFailure_CreatesCorrectReport()
    {
        var report = FailureReport.FromValidationFailure(
            "Response contained hallucinated facts",
            "Faithfulness",
            score: 0.3);

        Assert.Equal("Response contained hallucinated facts", report.WhyItFailed);
        Assert.Equal("Faithfulness", report.MetricName);
        Assert.Equal(0.3, report.Score);
        Assert.Single(report.Reasons);
    }

    [Fact]
    public void FromLLMError_CreatesCorrectReport()
    {
        var report = FailureReport.FromLLMError("Rate limit exceeded");

        Assert.Contains("LLM evaluation failed", report.WhyItFailed);
        Assert.True(report.IsCritical);
        Assert.Equal("LLMError", report.Reasons[0].Category);
        Assert.True(report.Suggestions.Count >= 2);
    }

    [Fact]
    public void ToFormattedReport_WithTimeline_IncludesTimeline()
    {
        var timeline = ToolCallTimeline.Create();
        timeline.TotalDuration = TimeSpan.FromMilliseconds(500);
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "test",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(100),
            Succeeded = false,
            ErrorMessage = "Test error"
        });

        var report = new FailureReport
        {
            WhyItFailed = "Test failed",
            Timeline = timeline
        };

        var formatted = report.ToFormattedReport(includeTimeline: true);

        Assert.Contains("Tool Call Timeline", formatted);
        Assert.Contains("test", formatted);
    }
}
