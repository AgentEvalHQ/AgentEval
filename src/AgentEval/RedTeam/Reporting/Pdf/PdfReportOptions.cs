// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using MigraDoc.DocumentObjectModel;

namespace AgentEval.RedTeam.Reporting.Pdf;

/// <summary>
/// Options for generating executive PDF reports.
/// </summary>
public record PdfReportOptions
{
    /// <summary>
    /// Name of the organization generating the report.
    /// </summary>
    public string? CompanyName { get; init; }

    /// <summary>
    /// Path to the company logo image file (PNG, JPG).
    /// </summary>
    public string? CompanyLogoPath { get; init; }

    /// <summary>
    /// Name of the agent being assessed.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Version of the agent being assessed.
    /// </summary>
    public string? AgentVersion { get; init; }

    /// <summary>
    /// PDF page size (default: A4).
    /// </summary>
    public PageSize PageSize { get; init; } = PageSize.A4;

    /// <summary>
    /// Report branding options.
    /// </summary>
    public BrandingOptions Branding { get; init; } = new();

    /// <summary>
    /// Include trend data comparing to baseline (if available).
    /// </summary>
    public bool IncludeTrends { get; init; }

    /// <summary>
    /// Baseline results for comparison (optional).
    /// </summary>
    public RedTeamResult? BaselineResults { get; init; }

    /// <summary>
    /// Include detailed attack results (adds extra pages).
    /// </summary>
    public bool IncludeDetailedResults { get; init; }

    /// <summary>
    /// Author name to include in PDF metadata.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Subject field for PDF metadata.
    /// </summary>
    public string? Subject { get; init; } = "AI Agent Security Assessment";
}

/// <summary>
/// Branding options for PDF reports.
/// </summary>
public record BrandingOptions
{
    /// <summary>
    /// Primary brand color (hex, e.g., "#0078D4").
    /// </summary>
    public string PrimaryColor { get; init; } = "#0078D4";

    /// <summary>
    /// Secondary brand color (hex).
    /// </summary>
    public string SecondaryColor { get; init; } = "#2B579A";

    /// <summary>
    /// Font family for text.
    /// </summary>
    public string FontFamily { get; init; } = "Arial";

    /// <summary>
    /// Gets as MigraDoc Color.
    /// </summary>
    internal Color GetPrimaryColor() => ParseHexColor(PrimaryColor);

    /// <summary>
    /// Gets as MigraDoc Color.
    /// </summary>
    internal Color GetSecondaryColor() => ParseHexColor(SecondaryColor);

    private static Color ParseHexColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return Colors.DarkBlue;

        hex = hex.TrimStart('#');
        if (hex.Length != 6)
            return Colors.DarkBlue;

        var r = Convert.ToByte(hex[..2], 16);
        var g = Convert.ToByte(hex[2..4], 16);
        var b = Convert.ToByte(hex[4..6], 16);

        return new Color(r, g, b);
    }
}

/// <summary>
/// PDF page size options.
/// </summary>
public enum PageSize
{
    /// <summary>A4 paper (210 × 297 mm)</summary>
    A4,
    
    /// <summary>US Letter (8.5 × 11 inches)</summary>
    Letter,
    
    /// <summary>US Legal (8.5 × 14 inches)</summary>
    Legal
}
