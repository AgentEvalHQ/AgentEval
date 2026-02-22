// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// Interface for compliance reports mapping scan results to security frameworks.
/// </summary>
public interface IComplianceReport
{
    /// <summary>Framework name (e.g., "OWASP LLM Top 10", "MITRE ATLAS").</summary>
    string FrameworkName { get; }

    /// <summary>Framework version.</summary>
    string FrameworkVersion { get; }

    /// <summary>Agent name from scan.</summary>
    string AgentName { get; }

    /// <summary>When the report was generated.</summary>
    DateTimeOffset GeneratedAt { get; }

    /// <summary>Overall compliance pass rate (0-100).</summary>
    double ComplianceRate { get; }

    /// <summary>Risk level based on compliance.</summary>
    RiskLevel RiskLevel { get; }

    /// <summary>Export to Markdown format.</summary>
    string ToMarkdown();

    /// <summary>Export to JSON format.</summary>
    string ToJson();
}

/// <summary>Risk level assessment based on compliance results.</summary>
public enum RiskLevel
{
    /// <summary>No significant issues found.</summary>
    Low,

    /// <summary>Some issues found but manageable.</summary>
    Moderate,

    /// <summary>Significant issues requiring attention.</summary>
    High,

    /// <summary>Critical issues requiring immediate action.</summary>
    Critical
}

/// <summary>
/// Interface for generating compliance reports from scan results.
/// </summary>
/// <typeparam name="TReport">Type of report generated.</typeparam>
public interface IComplianceReporter<out TReport> where TReport : IComplianceReport
{
    /// <summary>
    /// Generate a compliance report from scan results.
    /// </summary>
    /// <param name="result">Red team scan result.</param>
    /// <param name="options">Report generation options.</param>
    /// <returns>The compliance report.</returns>
    TReport GenerateReport(RedTeamResult result, ComplianceReportOptions? options = null);
}

/// <summary>
/// Options for compliance report generation.
/// </summary>
public class ComplianceReportOptions
{
    /// <summary>Include detailed findings in report.</summary>
    public bool IncludeDetailedFindings { get; init; } = true;

    /// <summary>Include actionable recommendations.</summary>
    public bool IncludeRecommendations { get; init; } = true;

    /// <summary>Include evidence (prompts/responses) in findings.</summary>
    public bool IncludeEvidence { get; init; } = false;

    /// <summary>Custom report title.</summary>
    public string? Title { get; init; }

    /// <summary>Company/organization name for branding.</summary>
    public string? OrganizationName { get; init; }
}
