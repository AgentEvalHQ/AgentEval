// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Exporters;

/// <summary>
/// Output format for evaluation results.
/// </summary>
public enum ExportFormat
{
    /// <summary>JSON format for programmatic consumption.</summary>
    Json,
    
    /// <summary>JUnit XML for CI/CD integration (GitHub Actions, Azure DevOps, Jenkins).</summary>
    Junit,
    
    /// <summary>Markdown for PR comments and documentation.</summary>
    Markdown,
    
    /// <summary>Visual Studio TRX format for .NET tooling.</summary>
    Trx,
    
    /// <summary>CSV format for Excel and business intelligence tools.</summary>
    Csv
}

/// <summary>
/// Interface for result exporters.
/// </summary>
public interface IResultExporter
{
    /// <summary>The format this exporter produces.</summary>
    ExportFormat Format { get; }
    
    /// <summary>File extension for this format (including the dot).</summary>
    string FileExtension { get; }
    
    /// <summary>MIME type for this format.</summary>
    string ContentType { get; }
    
    /// <summary>Export results to a stream.</summary>
    /// <param name="report">The evaluation report to export.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default);
}

/// <summary>
/// Factory for creating result exporters.
/// </summary>
public static class ResultExporterFactory
{
    /// <summary>
    /// Create an exporter for the specified format.
    /// </summary>
    public static IResultExporter Create(ExportFormat format) => format switch
    {
        ExportFormat.Json => new JsonExporter(),
        ExportFormat.Junit => new JUnitXmlExporter(),
        ExportFormat.Markdown => new MarkdownExporter(),
        ExportFormat.Trx => new TrxExporter(),
        ExportFormat.Csv => new CsvExporter(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format")
    };
    
    /// <summary>
    /// Create an exporter based on file extension.
    /// </summary>
    public static IResultExporter CreateFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".json" => new JsonExporter(),
        ".xml" => new JUnitXmlExporter(),
        ".md" or ".markdown" => new MarkdownExporter(),
        ".trx" => new TrxExporter(),
        _ => throw new ArgumentException($"Unknown file extension: {extension}", nameof(extension))
    };
}
