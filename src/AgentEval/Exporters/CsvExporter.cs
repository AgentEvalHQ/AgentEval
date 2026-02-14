// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

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
        
        // Write header row
        await writer.WriteLineAsync("RunId,TestName,Category,Score,Passed,Skipped,DurationMs,Error,AgentName,AgentModel");
        
        // Write data rows
        foreach (var result in report.TestResults)
        {
            var row = $"{EscapeCsvField(report.RunId)}," +
                     $"{EscapeCsvField(result.Name)}," +
                     $"{EscapeCsvField(result.Category ?? "")}," +
                     $"{result.Score:F2}," +
                     $"{result.Passed}," +
                     $"{result.Skipped}," +
                     $"{result.DurationMs}," +
                     $"{EscapeCsvField(result.Error ?? "")}," +
                     $"{EscapeCsvField(report.Agent?.Name ?? "")}," +
                     $"{EscapeCsvField(report.Agent?.Model ?? "")}";
            
            await writer.WriteLineAsync(row);
        }
        
        // Write summary row
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("# Summary");
        await writer.WriteLineAsync($"Name,{EscapeCsvField(report.Name ?? "")}");
        await writer.WriteLineAsync($"StartTime,{report.StartTime:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"EndTime,{report.EndTime:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"DurationMs,{report.Duration.TotalMilliseconds:F0}");
        await writer.WriteLineAsync($"TotalTests,{report.TotalTests}");
        await writer.WriteLineAsync($"PassedTests,{report.PassedTests}");
        await writer.WriteLineAsync($"FailedTests,{report.FailedTests}");
        await writer.WriteLineAsync($"SkippedTests,{report.SkippedTests}");
        await writer.WriteLineAsync($"PassRate,{report.PassRate:F4}");
        await writer.WriteLineAsync($"OverallScore,{report.OverallScore:F2}");
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