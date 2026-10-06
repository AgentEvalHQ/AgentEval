// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

/// <summary>
/// A memory benchmark category that crashed is not one the agent does not support. Before the split both were a
/// skip, the weights renormalised around the crash, and the grade could not tell "87% across the board" from "87%
/// across the third that ran".
/// </summary>
public class MemoryBenchmarkCrashVsUnsupportedTests
{
    private static BenchmarkCategoryResult Category(string name, double score, double weight,
        bool skipped = false, bool errored = false, string? reason = null) => new()
    {
        CategoryName = name,
        Score = score,
        Weight = weight,
        ScenarioType = BenchmarkScenarioType.BasicRetention,
        Duration = TimeSpan.FromSeconds(1),
        Skipped = skipped || errored,
        Errored = errored,
        SkipReason = reason,
    };

    private static MemoryBenchmarkResult Result(params BenchmarkCategoryResult[] categories) => new()
    {
        BenchmarkName = "Test",
        Duration = TimeSpan.FromSeconds(3),
        CategoryResults = categories,
    };

    [Fact]
    public void ACrashedCategory_CountsAsZero_InsteadOfLeavingTheWeights()
    {
        var result = Result(
            Category("Ran", 90, 1.0 / 3),
            Category("Crashed", 0, 2.0 / 3, errored: true, reason: "Error: socket closed"));

        Assert.Equal(30, result.OverallScore, precision: 6);     // 90 × 1/3 + 0 × 2/3
        Assert.Equal(90, result.CapabilityScore, precision: 6);  // the part that ran, for diagnosis
        Assert.False(result.Passed);
        Assert.Equal(["Crashed"], result.ErroredCategories);
    }

    [Fact]
    public void AnUnsupportedCategory_StillLeavesTheWeights()
    {
        var result = Result(
            Category("Ran", 90, 1.0 / 3),
            Category("Unsupported", 0, 2.0 / 3, skipped: true, reason: "requires ISessionResettableAgent"));

        Assert.Equal(90, result.OverallScore, precision: 6);
        Assert.Equal(90, result.CapabilityScore, precision: 6);
        Assert.Empty(result.ErroredCategories);
    }

    [Fact]
    public void TheRecommendations_TellACrashFromAnUnsupportedCategory()
    {
        var result = Result(
            Category("Ran", 90, 0.4),
            Category("Crashed", 0, 0.3, errored: true, reason: "Error: socket closed"),
            Category("Unsupported", 0, 0.3, skipped: true));

        Assert.Contains(result.Recommendations, r => r.StartsWith("Crashed was not measured (Error: socket closed). It counts as 0", StringComparison.Ordinal));
        Assert.Contains("Unsupported was skipped: not supported by this agent.", result.Recommendations);
        Assert.DoesNotContain(result.Recommendations, r => r.StartsWith("Crashed was skipped", StringComparison.Ordinal));
    }
}
