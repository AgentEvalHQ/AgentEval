// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Interface for exporting red team results to various formats.
/// </summary>
public interface IReportExporter
{
    /// <summary>
    /// The name of this exporter format (e.g., "JSON", "SARIF", "Markdown").
    /// </summary>
    string FormatName { get; }

    /// <summary>
    /// The file extension for this format (e.g., ".json", ".sarif", ".md").
    /// </summary>
    string FileExtension { get; }

    /// <summary>
    /// The MIME type for this format (e.g., "application/json").
    /// </summary>
    string MimeType { get; }

    /// <summary>
    /// Exports the result to a string.
    /// </summary>
    /// <param name="result">The red team result to export.</param>
    /// <returns>String representation in this format.</returns>
    string Export(RedTeamResult result);

    /// <summary>
    /// Exports the result to a file.
    /// </summary>
    /// <param name="result">The red team result to export.</param>
    /// <param name="filePath">Path to write the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default);
}
