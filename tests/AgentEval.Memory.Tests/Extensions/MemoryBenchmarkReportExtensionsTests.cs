// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;
using Xunit;

namespace AgentEval.Memory.Tests.Extensions;

public class MemoryBenchmarkReportExtensionsTests
{
    [Fact]
    public void ToEvaluationReport_TestCounts_Correct()
    {
        var result = CreateResult(passed: 2, failed: 1, skipped: 1);
        var report = result.ToEvaluationReport();

        Assert.Equal(4, report.TotalTests);
        Assert.Equal(2, report.PassedTests);   // score >= 70 and not skipped
        Assert.Equal(1, report.FailedTests);   // score < 70 and not skipped
        Assert.Equal(1, report.SkippedTests);
    }

    [Fact]
    public void ToEvaluationReport_OverallScore_MapsCorrectly()
    {
        var result = CreateSimpleResult(85);
        var report = result.ToEvaluationReport();

        Assert.Equal(result.OverallScore, report.OverallScore);
    }

    [Fact]
    public void ToEvaluationReport_AgentInfo_PopulatedWhenProvided()
    {
        var result = CreateSimpleResult(80);
        var report = result.ToEvaluationReport(agentName: "TestAgent", modelName: "gpt-4o");

        Assert.NotNull(report.Agent);
        Assert.Equal("TestAgent", report.Agent.Name);
        Assert.Equal("gpt-4o", report.Agent.Model);
    }

    [Fact]
    public void ToEvaluationReport_AgentInfo_NullWhenNotProvided()
    {
        var result = CreateSimpleResult(80);
        var report = result.ToEvaluationReport();

        Assert.Null(report.Agent);
    }

    [Fact]
    public void ToEvaluationReport_CategoryMapsToTestResult()
    {
        var result = CreateSimpleResult(85);
        var report = result.ToEvaluationReport();

        Assert.Single(report.TestResults);
        var tr = report.TestResults[0];
        Assert.Equal("Basic Retention", tr.Name);
        Assert.Equal("MemoryBenchmark", tr.Category);
        Assert.Equal(85, tr.Score);
        Assert.True(tr.Passed);
        Assert.False(tr.Skipped);
    }

    [Fact]
    public void ToEvaluationReport_Metadata_ContainsGradeStarsBenchmarkType()
    {
        var result = CreateSimpleResult(85);
        var report = result.ToEvaluationReport();

        Assert.Equal("B", report.Metadata["Grade"]);
        Assert.Equal("4", report.Metadata["Stars"]);
        Assert.Equal("MemoryBenchmark", report.Metadata["BenchmarkType"]);
    }

    [Fact]
    public void ToEvaluationReport_SkippedCategory_HasErrorAndSkippedFlag()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Full",
            Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Cross-Session",
                    Score = 0,
                    Weight = 0.15,
                    ScenarioType = BenchmarkScenarioType.CrossSession,
                    Duration = TimeSpan.Zero,
                    Skipped = true,
                    SkipReason = "Agent does not implement ISessionResettableAgent"
                }
            ]
        };

        var report = result.ToEvaluationReport();
        var tr = report.TestResults[0];

        Assert.True(tr.Skipped);
        Assert.Equal("Agent does not implement ISessionResettableAgent", tr.Error);
    }

    [Fact]
    public void ToEvaluationReport_MetricScores_HasMemoryKey()
    {
        var result = CreateSimpleResult(80);
        var report = result.ToEvaluationReport();

        var tr = report.TestResults[0];
        Assert.True(tr.MetricScores.ContainsKey("memory_basicretention"));
        Assert.Equal(80, tr.MetricScores["memory_basicretention"]);
    }

    [Fact]
    public void ToEvaluationReport_BoundaryScore70_CountsAsPassed()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test", Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Boundary",
                    Score = 70.0,  // Exactly on the boundary
                    Weight = 1.0,
                    ScenarioType = BenchmarkScenarioType.BasicRetention,
                    Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        var report = result.ToEvaluationReport();
        Assert.Equal(1, report.PassedTests);
        Assert.Equal(0, report.FailedTests);
    }

    [Fact]
    public void ToEvaluationReport_Score69_CountsAsFailed()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test", Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Below",
                    Score = 69.99,
                    Weight = 1.0,
                    ScenarioType = BenchmarkScenarioType.BasicRetention,
                    Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        var report = result.ToEvaluationReport();
        Assert.Equal(0, report.PassedTests);
        Assert.Equal(1, report.FailedTests);
    }

    [Fact]
    public void ToEvaluationReport_MultiWordScenarioType_ProducesCorrectMetricKey()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test", Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Reach-Back Depth",
                    Score = 80,
                    Weight = 1.0,
                    ScenarioType = BenchmarkScenarioType.ReachBackDepth,
                    Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        var report = result.ToEvaluationReport();
        var tr = report.TestResults[0];
        // ScenarioType.ReachBackDepth.ToString() = "ReachBackDepth" → lowercase = "reachbackdepth"
        Assert.True(tr.MetricScores.ContainsKey("memory_reachbackdepth"));
    }

    [Fact]
    public void ToEvaluationReport_OnlyAgentName_CreatesAgentInfo()
    {
        var result = CreateSimpleResult(80);
        var report = result.ToEvaluationReport(agentName: "TestAgent");

        Assert.NotNull(report.Agent);
        Assert.Equal("TestAgent", report.Agent.Name);
        Assert.Null(report.Agent.Model);
    }

    [Fact]
    public void ToEvaluationReport_AllSkipped_ZeroPassedZeroFailed()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Test", Duration = TimeSpan.FromSeconds(1),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Skipped1", Score = 0, Weight = 0.5,
                    ScenarioType = BenchmarkScenarioType.CrossSession,
                    Duration = TimeSpan.Zero, Skipped = true, SkipReason = "No reset"
                },
                new BenchmarkCategoryResult
                {
                    CategoryName = "Skipped2", Score = 0, Weight = 0.5,
                    ScenarioType = BenchmarkScenarioType.ReducerFidelity,
                    Duration = TimeSpan.Zero, Skipped = true, SkipReason = "No reducer"
                }
            ]
        };

        var report = result.ToEvaluationReport();
        Assert.Equal(0, report.PassedTests);
        Assert.Equal(0, report.FailedTests);
        Assert.Equal(2, report.SkippedTests);
    }

    // --- Helpers ---

    private static MemoryBenchmarkResult CreateSimpleResult(double score) => new()
    {
        BenchmarkName = "Quick",
        Duration = TimeSpan.FromSeconds(3),
        CategoryResults =
        [
            new BenchmarkCategoryResult
            {
                CategoryName = "Basic Retention",
                Score = score,
                Weight = 1.0,
                ScenarioType = BenchmarkScenarioType.BasicRetention,
                Duration = TimeSpan.FromSeconds(3)
            }
        ]
    };

    private static MemoryBenchmarkResult CreateResult(int passed, int failed, int skipped)
    {
        var cats = new List<BenchmarkCategoryResult>();
        for (int i = 0; i < passed; i++)
            cats.Add(new BenchmarkCategoryResult
            {
                CategoryName = $"Passed-{i}",
                Score = 85,
                Weight = 0.25,
                ScenarioType = BenchmarkScenarioType.BasicRetention,
                Duration = TimeSpan.FromSeconds(1)
            });
        for (int i = 0; i < failed; i++)
            cats.Add(new BenchmarkCategoryResult
            {
                CategoryName = $"Failed-{i}",
                Score = 45,
                Weight = 0.25,
                ScenarioType = BenchmarkScenarioType.NoiseResilience,
                Duration = TimeSpan.FromSeconds(1)
            });
        for (int i = 0; i < skipped; i++)
            cats.Add(new BenchmarkCategoryResult
            {
                CategoryName = $"Skipped-{i}",
                Score = 0,
                Weight = 0.25,
                ScenarioType = BenchmarkScenarioType.CrossSession,
                Duration = TimeSpan.Zero,
                Skipped = true,
                SkipReason = "Test skip"
            });

        return new MemoryBenchmarkResult
        {
            BenchmarkName = "Test",
            Duration = TimeSpan.FromSeconds(3),
            CategoryResults = cats
        };
    }
}
