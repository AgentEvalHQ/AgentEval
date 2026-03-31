// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Models;

/// <summary>
/// Extension methods for <see cref="TestSummary"/> to bridge the evaluation
/// pipeline to the export pipeline.
/// </summary>
/// <remarks>
/// The evaluation pipeline produces <see cref="TestSummary"/> (from <c>RunBatchAsync</c>).
/// The export pipeline consumes <see cref="EvaluationReport"/> (via <c>IResultExporter</c>).
/// This extension bridges them in a single call, enabling a clean pipeline:
/// <code>
/// var summary = await harness.RunBatchAsync(agent, testCases);
/// var report = summary.ToEvaluationReport(agentName: "GPT-4o");
/// await exporter.ExportAsync(report, stream);
/// </code>
/// </remarks>
public static class TestSummaryExtensions
{
    /// <summary>
    /// Converts a <see cref="TestSummary"/> to an <see cref="EvaluationReport"/>
    /// suitable for all exporters (JSON, JUnit XML, Markdown, TRX, CSV).
    /// </summary>
    /// <param name="summary">The test summary from a batch evaluation run.</param>
    /// <param name="agentName">Optional agent name for the report.</param>
    /// <param name="modelName">Optional model identifier (e.g., "gpt-4o").</param>
    /// <param name="endpoint">Optional endpoint URL for provenance tracking.</param>
    /// <returns>An <see cref="EvaluationReport"/> ready for export.</returns>
    public static EvaluationReport ToEvaluationReport(
        this TestSummary summary,
        string? agentName = null,
        string? modelName = null,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // Derive time boundaries from performance data when available
        var resultsWithPerf = summary.Results.Where(r => r.Performance != null).ToList();
        var startTime = resultsWithPerf.Count > 0
            ? resultsWithPerf.Min(r => r.Performance!.StartTime)
            : DateTimeOffset.UtcNow;
        var endTime = resultsWithPerf.Count > 0
            ? resultsWithPerf.Max(r => r.Performance!.EndTime)
            : DateTimeOffset.UtcNow;

        return new EvaluationReport
        {
            Name = summary.SuiteName,
            TotalTests = summary.TotalCount,
            PassedTests = summary.PassedCount,
            FailedTests = summary.FailedCount,
            OverallScore = summary.AverageScore,
            StartTime = startTime,
            EndTime = endTime,
            Agent = (agentName != null || modelName != null || endpoint != null)
                ? new AgentInfo
                {
                    Name = agentName,
                    Model = modelName,
                    Endpoint = endpoint
                }
                : null,
            Metadata = BuildMetadata(summary),
            TestResults = summary.Results.Select(r => MapTestResult(r, summary.SuiteName)).ToList()
        };
    }

    private static TestResultSummary MapTestResult(TestResult result, string suiteName)
    {
        return new TestResultSummary
        {
            Name = result.TestName,
            Category = suiteName,
            Score = result.Score,
            Passed = result.Passed,
            DurationMs = result.Performance != null
                ? (long)result.Performance.TotalDuration.TotalMilliseconds
                : 0,
            Error = BuildErrorMessage(result),
            StackTrace = result.Error?.StackTrace,
            Output = result.ActualOutput,
            MetricScores = result.MetricResults?
                .ToDictionary(m => m.MetricName, m => m.Score)
                ?? new Dictionary<string, double>()
        };
    }

    /// <summary>
    /// Builds the error message for a test result, prioritizing:
    /// 1. Exception message (if HasError)
    /// 2. FailureReport.WhyItFailed (if present, enriched with suggestions)
    /// 3. Details (for failed tests without errors or failure reports)
    /// </summary>
    private static string? BuildErrorMessage(TestResult result)
    {
        if (result.HasError)
            return result.Error!.Message;

        if (!result.Passed && result.Failure != null)
        {
            var message = result.Failure.WhyItFailed;
            if (result.Failure.Suggestions.Count > 0)
                message += " | Suggestions: " + string.Join("; ", result.Failure.Suggestions.Select(s => s.Description));
            return message;
        }

        return !result.Passed ? result.Details : null;
    }

    /// <summary>
    /// Builds metadata dictionary with summary-level statistics.
    /// </summary>
    private static Dictionary<string, string> BuildMetadata(TestSummary summary)
    {
        var metadata = new Dictionary<string, string>();

        if (summary.TotalDuration > TimeSpan.Zero)
            metadata["TotalDuration"] = summary.TotalDuration.ToString();

        if (summary.TotalCost > 0)
            metadata["TotalCost"] = summary.TotalCost.ToString("F6", CultureInfo.InvariantCulture);

        return metadata;
    }
}
