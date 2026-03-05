// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results as CSV (Comma-Separated Values).
/// Optimized for analysis in Excel, Power BI, and other business intelligence tools.
/// </summary>
public class CsvExporter : IResultExporter
{
    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Csv;
    
    /// <inheritdoc />
    public string FileExtension => ".csv";
    
    /// <inheritdoc />
    public string ContentType => "text/csv";

    /// <inheritdoc />
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        
        // Collect all unique metric names for dynamic columns
        var metricNames = report.TestResults
            .SelectMany(r => r.MetricScores.Keys)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        
        // Write header row
        var header = "RunId,TestName,Category,Score,Passed,Skipped,DurationMs,Error,AgentName,AgentModel";
        if (metricNames.Count > 0)
            header += "," + string.Join(",", metricNames);
        await writer.WriteLineAsync(header);
        
        // Write data rows
        foreach (var result in report.TestResults)
        {
            var row = $"{EscapeCsvField(report.RunId)}," +
                     $"{EscapeCsvField(result.Name)}," +
                     $"{EscapeCsvField(result.Category ?? "")}," +
                     $"{result.Score.ToString("F2", CultureInfo.InvariantCulture)}," +
                     $"{result.Passed}," +
                     $"{result.Skipped}," +
                     $"{result.DurationMs}," +
                     $"{EscapeCsvField(result.Error ?? "")}," +
                     $"{EscapeCsvField(report.Agent?.Name ?? "")}," +
                     $"{EscapeCsvField(report.Agent?.Model ?? "")}";
            
            // Append metric score values
            foreach (var metric in metricNames)
            {
                var value = result.MetricScores.TryGetValue(metric, out var s) ? s.ToString("F2", CultureInfo.InvariantCulture) : "";
                row += $",{value}";
            }
            
            await writer.WriteLineAsync(row);
        }
    }
    
    /// <summary>
    /// Export to a string.
    /// </summary>
    public async Task<string> ExportToStringAsync(EvaluationReport report, CancellationToken ct = default)
    {
        using var stream = new MemoryStream();
        await ExportAsync(report, stream, ct);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
    
    /// <summary>
    /// Escapes CSV field values that contain commas, quotes, or newlines.
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";
        
        // If field contains comma, quote, or newline, wrap in quotes and escape internal quotes
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        
        return field;
    }
}