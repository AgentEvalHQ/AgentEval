// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;
using AgentEval.Exporters.Models;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results to a structured directory (ADR-002).
/// <para>
/// Produces:
/// <list type="bullet">
///   <item><c>results.jsonl</c> — One JSON line per test result (streaming-friendly, append-friendly)</item>
///   <item><c>summary.json</c> — Aggregate statistics, per-metric distribution stats</item>
///   <item><c>run.json</c> — Run metadata: agent info, environment, timestamp, duration</item>
///   <item><i>(original filename)</i> — Copy of original config/dataset file with original filename preserved (when provided)</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Programmatic usage (core library):</b>
/// <code>
/// var exporter = new DirectoryExporter();
/// await exporter.ExportToDirectoryAsync(report, "./results/baseline");
/// </code>
/// </para>
/// <para>
/// <b>CLI usage:</b>
/// <code>
/// agenteval eval --azure --model gpt-4o --dataset tests.yaml --output-dir ./results/baseline
/// </code>
/// </para>
/// <para>
/// The <see cref="IResultExporter.ExportAsync"/> stream-based method writes <c>summary.json</c>
/// content to the stream for compatibility with the exporter framework. For the full directory
/// output, use <see cref="ExportToDirectoryAsync"/>.
/// </para>
/// </remarks>
public sealed class DirectoryExporter : IResultExporter
{
    /// <summary>Well-known file name for per-test results in JSON Lines format.</summary>
    public const string ResultsFileName = "results.jsonl";
    
    /// <summary>Well-known file name for aggregate statistics.</summary>
    public const string SummaryFileName = "summary.json";
    
    /// <summary>Well-known file name for run metadata.</summary>
    public const string RunFileName = "run.json";
    
    /// <summary>Default file name for config copy (used when original name cannot be determined).</summary>
    public const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions s_jsonlOptions = new()
    {
        WriteIndented = false,  // JSONL = no indentation, one object per line
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Directory;

    /// <inheritdoc />
    public string FormatName => "Directory";

    /// <inheritdoc />
    public string FileExtension => "";

    /// <inheritdoc />
    public string ContentType => "application/x-directory";

    /// <summary>
    /// Writes <c>summary.json</c> content to the stream for <see cref="IResultExporter"/> compatibility.
    /// For full directory export, use <see cref="ExportToDirectoryAsync"/> instead.
    /// </summary>
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        var summary = BuildSummary(report);
        await JsonSerializer.SerializeAsync(output, summary, s_jsonOptions, ct);
    }

    /// <summary>
    /// Exports a full structured directory with results.jsonl, summary.json, run.json, and optionally config.json.
    /// This is the primary entry point for programmatic usage.
    /// </summary>
    /// <param name="report">The evaluation report to export.</param>
    /// <param name="directoryPath">
    /// The directory to write results into. Created if it does not exist.
    /// Existing files in the directory will be overwritten.
    /// </param>
    /// <param name="configFilePath">
    /// Optional path to the original config/dataset file. If provided and the file exists,
    /// it is copied into the directory with its original filename preserved for reproducibility.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when report or directoryPath is null.</exception>
    public async Task ExportToDirectoryAsync(
        EvaluationReport report,
        string directoryPath,
        string? configFilePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        Directory.CreateDirectory(directoryPath);

        // 1. results.jsonl — one JSON object per line per test
        await WriteResultsJsonlAsync(report, directoryPath, ct);

        // 2. summary.json — aggregate statistics
        await WriteSummaryJsonAsync(report, directoryPath, ct);

        // 3. run.json — run metadata
        await WriteRunJsonAsync(report, directoryPath, ct);

        // 4. config copy — preserve original filename and extension for reproducibility
        if (configFilePath is not null && File.Exists(configFilePath))
        {
            var configDestName = Path.GetFileName(configFilePath);
            var destPath = Path.Combine(directoryPath, configDestName);
            File.Copy(configFilePath, destPath, overwrite: true);
        }
    }

    /// <summary>
    /// Generates a suggested directory name based on the report's timestamp and model.
    /// Format: <c>yyyy-MM-dd_HH-mm-ss_model</c> (e.g., "2026-03-01_14-30-00_gpt-4o").
    /// </summary>
    /// <param name="report">The report to generate a name for.</param>
    /// <returns>A filesystem-safe directory name.</returns>
    public static string GenerateDirectoryName(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var timestamp = report.StartTime.ToString("yyyy-MM-dd_HH-mm-ss");
        var model = report.Agent?.Model?.Replace('/', '-').Replace('\\', '-').Replace(':', '-') ?? "unknown";
        return $"{timestamp}_{model}";
    }

    // ─── Private helpers ────────────────────────────────────────────────

    private static async Task WriteResultsJsonlAsync(
        EvaluationReport report, string directoryPath, CancellationToken ct)
    {
        var filePath = Path.Combine(directoryPath, ResultsFileName);
        var sb = new StringBuilder();

        foreach (var result in report.TestResults)
        {
            var line = new DirectoryTestResult
            {
                Name = result.Name,
                Category = result.Category,
                Passed = result.Passed,
                Skipped = result.Skipped,
                Score = result.Score,
                DurationMs = result.DurationMs,
                Error = result.Error,
                Metrics = result.MetricScores.Count > 0 ? result.MetricScores : null
            };

            sb.AppendLine(JsonSerializer.Serialize(line, s_jsonlOptions));
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct);
    }

    private static async Task WriteSummaryJsonAsync(
        EvaluationReport report, string directoryPath, CancellationToken ct)
    {
        var filePath = Path.Combine(directoryPath, SummaryFileName);
        var summary = BuildSummary(report);

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, summary, s_jsonOptions, ct);
    }

    private static async Task WriteRunJsonAsync(
        EvaluationReport report, string directoryPath, CancellationToken ct)
    {
        var filePath = Path.Combine(directoryPath, RunFileName);

        var runMetadata = new DirectoryRunMetadata
        {
            RunId = report.RunId,
            Name = report.Name,
            Timestamp = report.StartTime,
            Duration = report.Duration.ToString(@"hh\:mm\:ss"),
            Agent = report.Agent is not null ? new DirectoryAgentInfo
            {
                Name = report.Agent.Name,
                Model = report.Agent.Model,
                Version = report.Agent.Version
            } : null,
            Environment = new DirectoryEnvironmentInfo
            {
                Machine = System.Environment.MachineName,
                Os = RuntimeInformation.OSDescription,
                DotnetVersion = RuntimeInformation.FrameworkDescription
            },
            Parameters = report.Metadata.Count > 0 ? report.Metadata : null
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, runMetadata, s_jsonOptions, ct);
    }

    internal static DirectorySummary BuildSummary(EvaluationReport report)
    {
        var summary = new DirectorySummary
        {
            RunId = report.RunId,
            Name = report.Name,
            Timestamp = report.StartTime,
            Duration = report.Duration.ToString(@"hh\:mm\:ss"),
            OverallScore = report.OverallScore,
            Stats = new DirectorySummaryStats
            {
                Total = report.TotalTests,
                Passed = report.PassedTests,
                Failed = report.FailedTests,
                Skipped = report.SkippedTests,
                PassRate = report.TotalTests > 0
                    ? (double)report.PassedTests / report.TotalTests
                    : 0
            }
        };

        // Compute per-metric statistics from individual test results
        var metricValues = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var testResult in report.TestResults)
        {
            foreach (var (metricName, score) in testResult.MetricScores)
            {
                if (!metricValues.TryGetValue(metricName, out var values))
                {
                    values = new List<double>();
                    metricValues[metricName] = values;
                }
                values.Add(score);
            }
        }

        foreach (var (metricName, values) in metricValues)
        {
            var sorted = values.OrderBy(v => v).ToList();
            summary.Metrics[metricName] = new DirectoryMetricStats
            {
                Mean = values.Count > 0 ? values.Average() : 0,
                Min = values.Count > 0 ? values.Min() : 0,
                Max = values.Count > 0 ? values.Max() : 0,
                StdDev = CalculateStdDev(values),
                P50 = CalculatePercentile(sorted, 50),
                P95 = CalculatePercentile(sorted, 95),
                P99 = CalculatePercentile(sorted, 99),
                SampleSize = values.Count
            };
        }

        return summary;
    }

    /// <summary>
    /// Calculates sample standard deviation. Self-contained to avoid requiring DI
    /// for IStatisticsCalculator — keeps the exporter simple and standalone.
    /// </summary>
    private static double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        double sumSquaredDiff = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquaredDiff / (values.Count - 1));
    }

    /// <summary>
    /// Calculates a percentile using linear interpolation on a pre-sorted list.
    /// </summary>
    private static double CalculatePercentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        double index = (percentile / 100.0) * (sorted.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = Math.Min((int)Math.Ceiling(index), sorted.Count - 1);

        if (lower == upper) return sorted[lower];
        double fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
