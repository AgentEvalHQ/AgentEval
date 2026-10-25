// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Extensions;

/// <summary>
/// T71: a memory benchmark in which no category was measured has no score in its export report. The benchmark's own
/// OverallScore is a placeholder 0 then, and a skipped category's Score of 0 is a placeholder too.
/// </summary>
public class MemoryBenchmarkReportNotMeasuredTests
{
    private static BenchmarkCategoryResult Category(string name, double score, bool skipped = false, bool errored = false) => new()
    {
        CategoryName = name,
        Score = score,
        Weight = 0.5,
        ScenarioType = BenchmarkScenarioType.BasicRetention,
        Duration = TimeSpan.FromSeconds(1),
        Skipped = skipped,
        Errored = errored,
        SkipReason = skipped ? "Test skip" : null,
    };

    private static MemoryBenchmarkResult Result(params BenchmarkCategoryResult[] categories) => new()
    {
        BenchmarkName = "Test",
        Duration = TimeSpan.FromSeconds(2),
        CategoryResults = categories,
    };

    [Fact]
    public void EverythingSkipped_HasNoScore()
    {
        // Today: OverallScore 0 and each category's Score 0, exported as measured zeros.
        var report = Result(Category("a", 0, skipped: true), Category("b", 0, skipped: true)).ToEvaluationReport();

        Assert.Null(report.OverallScore);
        Assert.All(report.TestResults, t => Assert.Null(t.Score));
    }

    [Fact]
    public void EveryCategoryCrashed_HasNoScore()
    {
        // A crashed category is marked skipped and errored; with every one crashed nothing was measured. Today: 0.
        var report = Result(Category("a", 0, skipped: true, errored: true), Category("b", 0, skipped: true, errored: true))
            .ToEvaluationReport();

        Assert.Null(report.OverallScore);
    }

    [Fact]
    public void ASkippedCategory_HasNoScore_ButTheMeasuredOnesKeepTheirs()
    {
        // Guard on the measured part: the report keeps the benchmark's score and the measured category's 85.
        var result = Result(Category("measured", 85), Category("skipped", 0, skipped: true));

        var report = result.ToEvaluationReport();

        Assert.Equal(result.OverallScore, report.OverallScore);
        Assert.Equal(85.0, report.TestResults.Single(t => t.Name == "measured").Score);
        Assert.Null(report.TestResults.Single(t => t.Name == "skipped").Score);
    }
}
