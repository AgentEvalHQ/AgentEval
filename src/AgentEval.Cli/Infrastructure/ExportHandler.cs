// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// Routes evaluation reports to the correct exporter and output destination.
/// Supports format aliases (e.g., "xml" → JUnit, "md" → Markdown).
/// </summary>
internal static class ExportHandler
{
    private static readonly Dictionary<string, ExportFormat> s_formatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["json"] = ExportFormat.Json,
        ["junit"] = ExportFormat.Junit,
        ["xml"] = ExportFormat.Junit,       // alias
        ["markdown"] = ExportFormat.Markdown,
        ["md"] = ExportFormat.Markdown,     // alias
        ["trx"] = ExportFormat.Trx,
        ["csv"] = ExportFormat.Csv,
        ["directory"] = ExportFormat.Directory,
        ["dir"] = ExportFormat.Directory,   // alias
    };

    /// <summary>
    /// Exports the evaluation report to the specified format and destination.
    /// When <paramref name="outputFile"/> is null, writes to stdout for piping.
    /// </summary>
    /// <param name="report">The evaluation report to export.</param>
    /// <param name="format">Format name (json, junit, xml, markdown, md, trx, csv, directory, dir).</param>
    /// <param name="outputFile">Optional output file. When null, writes to stdout.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when format is unknown.</exception>
    public static async Task ExportAsync(
        EvaluationReport report,
        string format,
        FileInfo? outputFile,
        CancellationToken ct)
    {
        if (!s_formatMap.TryGetValue(format, out var exportFormat))
            throw new ArgumentException(
                $"Unknown format '{format}'. Valid formats: {string.Join(", ", s_formatMap.Keys.Order())}",
                nameof(format));

        // Directory format requires --output-dir, not --format + --output
        if (exportFormat == ExportFormat.Directory)
            throw new ArgumentException(
                $"The '{format}' format produces a structured directory (results.jsonl, summary.json, run.json). " +
                "Use --output-dir <path> instead of --format directory --output <file>.",
                nameof(format));

        var exporter = ResultExporterFactory.Create(exportFormat);

        if (outputFile is not null)
        {
            // Write to file — ensure parent directory exists
            outputFile.Directory?.Create();
            await using var stream = outputFile.Create();
            await exporter.ExportAsync(report, stream, ct);
        }
        else
        {
            // Write to stdout for piping (agenteval eval ... | jq .)
            await using var stream = Console.OpenStandardOutput();
            await exporter.ExportAsync(report, stream, ct);
        }
    }

    /// <summary>
    /// Exports the evaluation report to a structured directory (ADR-002 format).
    /// Produces results.jsonl, summary.json, run.json, and optionally config.json.
    /// </summary>
    /// <param name="report">The evaluation report to export.</param>
    /// <param name="outputDir">The directory to write results into.</param>
    /// <param name="configFilePath">Optional config file path to copy for reproducibility.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExportToDirectoryAsync(
        EvaluationReport report,
        DirectoryInfo outputDir,
        string? configFilePath,
        CancellationToken ct)
    {
        var exporter = new DirectoryExporter();
        await exporter.ExportToDirectoryAsync(report, outputDir.FullName, configFilePath, ct);
    }

    /// <summary>
    /// Returns the list of supported format names (for error messages).
    /// </summary>
    internal static IReadOnlyCollection<string> SupportedFormats => s_formatMap.Keys;
}
