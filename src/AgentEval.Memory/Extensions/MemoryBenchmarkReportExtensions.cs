// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
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

        var now = DateTimeOffset.UtcNow;
        // With no category measured (every one skipped, or crashed: a crashed category is marked skipped too), the
        // benchmark's OverallScore is a placeholder 0, so the report has no score.
        var anyMeasured = result.CategoryResults.Any(c => !c.Skipped);
        return new EvaluationReport
        {
            Name = result.BenchmarkName,
            TotalTests = result.CategoryResults.Count,
            PassedTests = result.CategoryResults.Count(c => !c.Skipped && !Incomplete(c) && c.Score >= 70),
            FailedTests = result.CategoryResults.Count(c => !c.Skipped && !Incomplete(c) && c.Score < 70),
            SkippedTests = result.CategoryResults.Count(c => c.Skipped || Incomplete(c)),
            OverallScore = result.IsComplete ? result.OverallScore : null,
            StartTime = now - result.Duration,
            EndTime = now,
            Agent = (agentName != null || modelName != null)
                ? new AgentInfo { Name = agentName, Model = modelName }
                : null,
            Metadata = new Dictionary<string, string>
            {
                ["Grade"] = result.IsComplete ? result.Grade : "not established",
                ["Stars"] = result.IsComplete ? result.Stars.ToString() : "not established",
                ["Completeness"] = result.IsComplete ? "complete" : "incomplete",
                ["ObservedOverallScore"] = anyMeasured
                    ? result.OverallScore.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not measured",
                ["BenchmarkType"] = "MemoryBenchmark"
            },
            TestResults = result.CategoryResults.Select(c => new TestResultSummary
            {
                Name = c.CategoryName,
                Category = "MemoryBenchmark",
                Score = c.Skipped ? null : c.Score,   // a skipped category's 0 is a placeholder, not a score
                Passed = !c.Skipped && !Incomplete(c) && c.Score >= 70,
                Skipped = c.Skipped || Incomplete(c),
                NotMeasured = Incomplete(c),
                DurationMs = (long)c.Duration.TotalMilliseconds,
                Error = Incomplete(c) ? GapReason(c) : c.SkipReason,
                // A skipped category's 0 is a placeholder, not a score: it goes under MetricsNotMeasured (#203 round 17).
                MetricScores = c.Skipped
                    ? new Dictionary<string, double>()
                    : new Dictionary<string, double> { [MetricName(c) + (Incomplete(c) ? "_observed" : "")] = c.Score },
                MetricsNotMeasured = c.Skipped || Incomplete(c)
                    ? new Dictionary<string, string> { [MetricName(c)] = Incomplete(c) ? GapReason(c) : c.SkipReason ?? "skipped" }
                    : new Dictionary<string, string>()
            }).ToList()
        };
    }

    private static string MetricName(BenchmarkCategoryResult c) => $"memory_{c.ScenarioType.ToString().ToLowerInvariant()}";

    private static bool Incomplete(BenchmarkCategoryResult c) => c.Errored || c.UnmeasuredQueries > 0;

    private static string GapReason(BenchmarkCategoryResult c) => c.Errored
        ? c.SkipReason ?? "Category execution produced no measurement."
        : $"Incomplete: {c.UnmeasuredQueries} question(s) unscored and excluded from the observed category score; no full-category verdict.";
}
