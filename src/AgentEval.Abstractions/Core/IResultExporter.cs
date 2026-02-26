// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

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
    
    /// <summary>
    /// String-based format name for registry lookup. Built-in exporters
    /// return the enum name; custom exporters override with their own.
    /// </summary>
    /// <remarks>
    /// This default interface member allows custom exporters to provide
    /// a format name without breaking existing implementations.
    /// </remarks>
    string FormatName => Format.ToString();
    
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
