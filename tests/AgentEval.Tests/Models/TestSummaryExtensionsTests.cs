// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// Tests for <see cref="TestSummaryExtensions.ToEvaluationReport"/>.
/// Validates the bridge from evaluation pipeline (TestSummary) to export pipeline (EvaluationReport).
/// </summary>
public class TestSummaryExtensionsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // BASIC MAPPING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_MapsCountsCorrectly()
    {
        // Arrange
        var summary = CreateSummary("Suite1",
            CreateResult("Test1", passed: true, score: 90),
            CreateResult("Test2", passed: true, score: 80),
            CreateResult("Test3", passed: false, score: 40));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal("Suite1", report.Name);
        Assert.Equal(3, report.TotalTests);
        Assert.Equal(2, report.PassedTests);
        Assert.Equal(1, report.FailedTests);
    }

    [Fact]
    public void ToEvaluationReport_MapsAverageScore()
    {
        // Arrange
        var summary = CreateSummary("Suite",
            CreateResult("A", passed: true, score: 100),
            CreateResult("B", passed: true, score: 50));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal(75.0, report.OverallScore!.Value, precision: 1);
    }

    [Fact]
    public void ToEvaluationReport_MapsAgentInfo()
    {
        // Arrange
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport(
            agentName: "GPT-4o",
            modelName: "gpt-4o-2024-08-06",
            endpoint: "https://api.openai.com/v1");

        // Assert
        Assert.NotNull(report.Agent);
        Assert.Equal("GPT-4o", report.Agent!.Name);
        Assert.Equal("gpt-4o-2024-08-06", report.Agent.Model);
        Assert.Equal("https://api.openai.com/v1", report.Agent.Endpoint);
    }

    [Fact]
    public void ToEvaluationReport_NoAgentInfo_WhenAllNull()
    {
        // Arrange
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Null(report.Agent);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST RESULT MAPPING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_MapsTestResults()
    {
        // Arrange
        var summary = CreateSummary("Suite",
            CreateResult("Test1", passed: true, score: 95, output: "Paris"),
            CreateResult("Test2", passed: false, score: 30, details: "Wrong answer"));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal(2, report.TestResults.Count);

        Assert.Equal("Test1", report.TestResults[0].Name);
        Assert.Equal(95, report.TestResults[0].Score);
        Assert.True(report.TestResults[0].Passed);
        Assert.Equal("Paris", report.TestResults[0].Output);
        Assert.Null(report.TestResults[0].Error);

        Assert.Equal("Test2", report.TestResults[1].Name);
        Assert.Equal(30, report.TestResults[1].Score);
        Assert.False(report.TestResults[1].Passed);
        Assert.Equal("Wrong answer", report.TestResults[1].Error);
    }

    [Fact]
    public void ToEvaluationReport_MapsErrorMessage()
    {
        // Arrange: throw-and-catch to populate StackTrace
        Exception caught;
        try { throw new InvalidOperationException("LLM timeout"); }
        catch (Exception ex) { caught = ex; }

        var result = new TestResult
        {
            TestName = "ErrorTest",
            Passed = false,
            Score = 0,
            Error = caught,
            Details = "Fallback details"
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: Error.Message takes precedence over Details
        Assert.Equal("LLM timeout", report.TestResults[0].Error);
        Assert.NotNull(report.TestResults[0].StackTrace);
    }

    [Fact]
    public void ToEvaluationReport_MapsPerformanceDuration()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var result = new TestResult
        {
            TestName = "PerfTest",
            Passed = true,
            Score = 90,
            Performance = new PerformanceMetrics
            {
                StartTime = now,
                EndTime = now.AddMilliseconds(1234)
            }
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal(1234, report.TestResults[0].DurationMs);
    }

    [Fact]
    public void ToEvaluationReport_NoPerformance_DurationIsNull()
    {
        // Arrange
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Null(report.TestResults[0].DurationMs);
    }

    // ═══════════════════════════════════════════════════════════════════
    // METRIC SCORES
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_MapsMetricScores()
    {
        // Arrange
        var result = new TestResult
        {
            TestName = "MetricTest",
            Passed = true,
            Score = 85,
            MetricResults = new[]
            {
                MetricResult.Pass("llm_faithfulness", 92.5),
                MetricResult.Pass("code_tool_success", 100.0),
                MetricResult.Fail("llm_relevance", "Low relevance", 45.0)
            }
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        var scores = report.TestResults[0].MetricScores;
        Assert.Equal(3, scores.Count);
        Assert.Equal(92.5, scores["llm_faithfulness"]);
        Assert.Equal(100.0, scores["code_tool_success"]);
        Assert.Equal(45.0, scores["llm_relevance"]);
    }

    [Fact]
    public void ToEvaluationReport_NoMetrics_EmptyDictionary()
    {
        // Arrange
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.NotNull(report.TestResults[0].MetricScores);
        Assert.Empty(report.TestResults[0].MetricScores);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TIME BOUNDARIES
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_DerivesTimeBoundariesFromPerformance()
    {
        // Arrange
        var t0 = new DateTimeOffset(2026, 2, 27, 10, 0, 0, TimeSpan.Zero);
        var results = new[]
        {
            new TestResult
            {
                TestName = "First",
                Passed = true,
                Score = 90,
                Performance = new PerformanceMetrics { StartTime = t0, EndTime = t0.AddSeconds(2) }
            },
            new TestResult
            {
                TestName = "Second",
                Passed = true,
                Score = 85,
                Performance = new PerformanceMetrics
                {
                    StartTime = t0.AddSeconds(3),
                    EndTime = t0.AddSeconds(5)
                }
            }
        };
        var summary = new TestSummary("TimeSuite", results);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: StartTime = earliest, EndTime = latest
        Assert.Equal(t0, report.StartTime);
        Assert.Equal(t0.AddSeconds(5), report.EndTime);
    }

    [Fact]
    public void ToEvaluationReport_NoPerformance_UsesUtcNow()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport();
        var after = DateTimeOffset.UtcNow;

        // Assert: times should be approximately now
        Assert.InRange(report.StartTime, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.InRange(report.EndTime, before.AddSeconds(-1), after.AddSeconds(1));
    }

    // ═══════════════════════════════════════════════════════════════════
    // METADATA (R1)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_MapsMetadata_TotalDurationAndTotalCost()
    {
        // Arrange: Results with performance data to generate non-zero TotalDuration/TotalCost
        var t0 = DateTimeOffset.UtcNow;
        var results = new[]
        {
            new TestResult
            {
                TestName = "T1",
                Passed = true,
                Score = 90,
                Performance = new PerformanceMetrics
                {
                    StartTime = t0,
                    EndTime = t0.AddSeconds(2),
                    EstimatedCost = 0.005m
                }
            },
            new TestResult
            {
                TestName = "T2",
                Passed = true,
                Score = 80,
                Performance = new PerformanceMetrics
                {
                    StartTime = t0.AddSeconds(3),
                    EndTime = t0.AddSeconds(5),
                    EstimatedCost = 0.003m
                }
            }
        };
        var summary = new TestSummary("MetadataSuite", results);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.True(report.Metadata.ContainsKey("TotalDuration"));
        Assert.True(report.Metadata.ContainsKey("TotalCost"));
        Assert.Equal("0.008000", report.Metadata["TotalCost"]);
    }

    [Fact]
    public void ToEvaluationReport_Metadata_EmptyWhenNoPerformance()
    {
        // Arrange: No performance data → TotalDuration and TotalCost are null (not recorded)
        var summary = CreateSummary("Suite", CreateResult("T", passed: true, score: 90));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: Neither key should be present (not recorded)
        Assert.False(report.Metadata.ContainsKey("TotalDuration"));
        Assert.False(report.Metadata.ContainsKey("TotalCost"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // CATEGORY MAPPING (R2)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_MapsCategoryToSuiteName()
    {
        // Arrange
        var summary = CreateSummary("MyCategorySuite",
            CreateResult("T1", passed: true, score: 90),
            CreateResult("T2", passed: false, score: 40));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: All test results should have Category = suite name
        Assert.All(report.TestResults, r => Assert.Equal("MyCategorySuite", r.Category));
    }

    // ═══════════════════════════════════════════════════════════════════
    // FAILURE REPORT ENRICHMENT (R3)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_FailureReport_MapsWhyItFailed()
    {
        // Arrange
        var result = new TestResult
        {
            TestName = "FailTest",
            Passed = false,
            Score = 20,
            Details = "Generic details",
            Failure = new FailureReport
            {
                WhyItFailed = "The agent called SecurityTool before FeatureTool"
            }
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: FailureReport.WhyItFailed takes precedence over Details
        Assert.Equal("The agent called SecurityTool before FeatureTool", report.TestResults[0].Error);
    }

    [Fact]
    public void ToEvaluationReport_FailureReport_IncludesSuggestions()
    {
        // Arrange
        var failure = new FailureReport
        {
            WhyItFailed = "Tool order wrong"
        };
        failure.AddSuggestion(new FixSuggestion
        {
            Title = "Reorder tools",
            Description = "Call FeatureTool before SecurityTool",
            Confidence = 0.9
        });
        failure.AddSuggestion(new FixSuggestion
        {
            Title = "Update prompt",
            Description = "Add ordering constraint to system prompt",
            Confidence = 0.7
        });

        var result = new TestResult
        {
            TestName = "SuggestionTest",
            Passed = false,
            Score = 30,
            Failure = failure
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: Error should contain WhyItFailed + Suggestions
        var error = report.TestResults[0].Error!;
        Assert.Contains("Tool order wrong", error);
        Assert.Contains("Suggestions:", error);
        Assert.Contains("Call FeatureTool before SecurityTool", error);
        Assert.Contains("Add ordering constraint to system prompt", error);
    }

    [Fact]
    public void ToEvaluationReport_ErrorTakesPrecedenceOverFailureReport()
    {
        // Arrange: Both Error and Failure set — Error.Message should win
        Exception caught;
        try { throw new InvalidOperationException("API timeout"); }
        catch (Exception ex) { caught = ex; }

        var result = new TestResult
        {
            TestName = "PrecedenceTest",
            Passed = false,
            Score = 0,
            Error = caught,
            Failure = new FailureReport { WhyItFailed = "Agent did not respond" }
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: Exception message wins
        Assert.Equal("API timeout", report.TestResults[0].Error);
    }

    [Fact]
    public void ToEvaluationReport_FailedWithoutFailureReport_UsesDetails()
    {
        // Arrange: Failed but no Failure and no Error — falls back to Details
        var result = new TestResult
        {
            TestName = "FallbackTest",
            Passed = false,
            Score = 40,
            Details = "Fallback details here"
        };
        var summary = CreateSummary("Suite", result);

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal("Fallback details here", report.TestResults[0].Error);
    }

    // ═══════════════════════════════════════════════════════════════════
    // EDGE CASES
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToEvaluationReport_EmptyResults_ProducesEmptyReport()
    {
        // Arrange
        var summary = new TestSummary("EmptySuite", Array.Empty<TestResult>());

        // Act
        var report = summary.ToEvaluationReport();

        // Assert
        Assert.Equal("EmptySuite", report.Name);
        Assert.Equal(0, report.TotalTests);
        Assert.Equal(0, report.PassedTests);
        Assert.Equal(0, report.FailedTests);
        Assert.Null(report.OverallScore);
        Assert.Empty(report.TestResults);
    }

    [Fact]
    public void ToEvaluationReport_NullSummary_Throws()
    {
        // Arrange
        TestSummary? summary = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => summary!.ToEvaluationReport());
    }

    [Fact]
    public void ToEvaluationReport_HasRunId()
    {
        // Arrange
        var summary = CreateSummary("Suite", CreateResult("Test", passed: true, score: 80));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: RunId is auto-generated, non-empty
        Assert.False(string.IsNullOrEmpty(report.RunId));
        Assert.Equal(8, report.RunId.Length);
    }

    [Fact]
    public void ToEvaluationReport_PassRate_CalculatedCorrectly()
    {
        // Arrange
        var summary = CreateSummary("Suite",
            CreateResult("A", passed: true, score: 90),
            CreateResult("B", passed: true, score: 80),
            CreateResult("C", passed: false, score: 40),
            CreateResult("D", passed: true, score: 70));

        // Act
        var report = summary.ToEvaluationReport();

        // Assert: 3 out of 4 = 75%
        Assert.Equal(75.0, report.PassRate!.Value, precision: 1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static TestSummary CreateSummary(string name, params TestResult[] results)
    {
        return new TestSummary(name, results);
    }

    private static TestResult CreateResult(
        string name,
        bool passed,
        int score,
        string? output = null,
        string? details = null)
    {
        return new TestResult
        {
            TestName = name,
            Passed = passed,
            Score = score,
            ActualOutput = output,
            Details = details ?? (passed ? "Passed" : "Failed")
        };
    }
}
