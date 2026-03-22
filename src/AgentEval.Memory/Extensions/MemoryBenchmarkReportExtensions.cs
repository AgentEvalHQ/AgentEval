// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Models;

namespace AgentEval.Memory.Extensions;

/// <summary>
/// Bridge from <see cref="MemoryBenchmarkResult"/> to the AgentEval export pipeline.
/// Converts to <see cref="EvaluationReport"/> so all 6 existing exporters
/// (JSON, CSV, Markdown, JUnit XML, TRX, Directory) work automatically.
/// <para>
/// Follows the same pattern as <c>TestSummaryExtensions.ToEvaluationReport()</c>
/// in AgentEval.Abstractions.
/// </para>
/// </summary>
public static class MemoryBenchmarkReportExtensions
{
    /// <summary>
    /// Converts a memory benchmark result to an <see cref="EvaluationReport"/>
    /// suitable for all existing AgentEval exporters.
    /// </summary>
    /// <param name="result">The benchmark result to convert.</param>
    /// <param name="agentName">Optional agent name for the report.</param>
    /// <param name="modelName">Optional model identifier (e.g., "gpt-4o").</param>
    /// <returns>An <see cref="EvaluationReport"/> ready for export.</returns>
    public static EvaluationReport ToEvaluationReport(
        this MemoryBenchmarkResult result,
        string? agentName = null,
        string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new EvaluationReport
        {
            Name = result.BenchmarkName,
            TotalTests = result.CategoryResults.Count,
            PassedTests = result.CategoryResults.Count(c => !c.Skipped && c.Score >= 70),
            FailedTests = result.CategoryResults.Count(c => !c.Skipped && c.Score < 70),
            SkippedTests = result.CategoryResults.Count(c => c.Skipped),
            OverallScore = result.OverallScore,
            StartTime = DateTimeOffset.UtcNow - result.Duration,
            EndTime = DateTimeOffset.UtcNow,
            Agent = (agentName != null || modelName != null)
                ? new AgentInfo { Name = agentName, Model = modelName }
                : null,
            Metadata = new Dictionary<string, string>
            {
                ["Grade"] = result.Grade,
                ["Stars"] = result.Stars.ToString(),
                ["BenchmarkType"] = "MemoryBenchmark"
            },
            TestResults = result.CategoryResults.Select(c => new TestResultSummary
            {
                Name = c.CategoryName,
                Category = "MemoryBenchmark",
                Score = c.Score,
                Passed = !c.Skipped && c.Score >= 70,
                Skipped = c.Skipped,
                DurationMs = (long)c.Duration.TotalMilliseconds,
                Error = c.SkipReason,
                MetricScores = new Dictionary<string, double>
                {
                    [$"memory_{c.ScenarioType.ToString().ToLowerInvariant()}"] = c.Score
                }
            }).ToList()
        };
    }
}
