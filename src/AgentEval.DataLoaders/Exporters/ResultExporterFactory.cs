// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Exporters;

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
        ".csv" => new CsvExporter(),
        _ => throw new ArgumentException($"Unknown file extension: {extension}", nameof(extension))
    };
}
