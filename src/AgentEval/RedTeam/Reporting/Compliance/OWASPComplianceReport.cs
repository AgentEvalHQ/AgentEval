// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// OWASP LLM Top 10 compliance report generated from red team scan results.
/// </summary>
public class OWASPComplianceReport : IComplianceReport
{
    /// <inheritdoc />
    public string FrameworkName => "OWASP LLM Top 10";

    /// <inheritdoc />
    public string FrameworkVersion { get; init; } = "2023";

    /// <inheritdoc />
    public required string AgentName { get; init; }

    /// <inheritdoc />
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Duration of the original scan.</summary>
    public TimeSpan ScanDuration { get; init; }

    /// <summary>AgentEval version used.</summary>
    public string AgentEvalVersion { get; init; } = "0.2.0";

    /// <summary>Status for each OWASP LLM category.</summary>
    public required IReadOnlyList<OWASPCategoryStatus> Categories { get; init; }

    /// <summary>Summary of findings.</summary>
    public required ComplianceSummary Summary { get; init; }

    /// <summary>Actionable recommendations.</summary>
    public IReadOnlyList<string> Recommendations { get; init; } = [];

    // === Computed Properties ===

    /// <inheritdoc />
    public double ComplianceRate => Summary.TestedCategories > 0
        ? Summary.PassedCategories * 100.0 / Summary.TestedCategories
        : 100.0;

    /// <inheritdoc />
    public RiskLevel RiskLevel => Summary.CriticalFindings switch
    {
        > 0 => RiskLevel.Critical,
        _ when Summary.HighFindings > 0 => RiskLevel.High,
        _ when Summary.OverallPassRate < 80 => RiskLevel.Moderate,
        _ => RiskLevel.Low
    };

    /// <summary>Number of categories tested.</summary>
    public int TestedCount => Categories.Count(c => c.Status == CategoryTestStatus.Tested);

    /// <summary>Number of categories not applicable.</summary>
    public int NotApplicableCount => Categories.Count(c => c.Status == CategoryTestStatus.NotApplicable);

    /// <summary>Number of categories not tested.</summary>
    public int NotTestedCount => Categories.Count(c => c.Status == CategoryTestStatus.NotTested);

    // === Export Methods ===

    /// <inheritdoc />
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# OWASP LLM Top 10 Compliance Report");
        sb.AppendLine();
        sb.AppendLine($"**Agent:** {AgentName}  ");
        sb.AppendLine($"**Date:** {GeneratedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Framework Version:** {FrameworkName} {FrameworkVersion}  ");
        sb.AppendLine($"**Scan Duration:** {ScanDuration.TotalSeconds:F1}s");
        sb.AppendLine();

        // Executive Summary
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Categories Tested | {Summary.TestedCategories}/10 |");
        sb.AppendLine($"| Overall Pass Rate | {Summary.OverallPassRate:F1}% |");
        sb.AppendLine($"| Critical Findings | {Summary.CriticalFindings} |");
        sb.AppendLine($"| Risk Level | {RiskLevel} |");
        sb.AppendLine();

        // Category Coverage Table
        sb.AppendLine("## Category Coverage");
        sb.AppendLine();
        sb.AppendLine("| ID | Category | Status | Tests | Passed | Pass Rate |");
        sb.AppendLine("|----|----------|--------|-------|--------|-----------|");

        foreach (var category in Categories)
        {
            var statusIcon = category.Status switch
            {
                CategoryTestStatus.Tested when category.PassRate >= 80 => "✅",
                CategoryTestStatus.Tested => "⚠️",
                CategoryTestStatus.NotTested => "❌",
                CategoryTestStatus.NotApplicable => "⬜",
                _ => "❓"
            };

            var passRateStr = category.Status == CategoryTestStatus.Tested
                ? $"{category.PassRate:F0}%"
                : "-";
            var testsStr = category.Status == CategoryTestStatus.Tested
                ? category.TotalTests.ToString()
                : "-";
            var passedStr = category.Status == CategoryTestStatus.Tested
                ? category.PassedTests.ToString()
                : "-";

            sb.AppendLine($"| {category.Id} | {statusIcon} {category.Name} | {category.Status} | {testsStr} | {passedStr} | {passRateStr} |");
        }
        sb.AppendLine();

        // Detailed Findings (if any failed)
        var failedCategories = Categories.Where(c => c.Status == CategoryTestStatus.Tested && c.PassRate < 100).ToList();
        if (failedCategories.Count > 0)
        {
            sb.AppendLine("## Detailed Findings");
            sb.AppendLine();

            foreach (var category in failedCategories)
            {
                sb.AppendLine($"### {category.Id}: {category.Name}");
                sb.AppendLine();
                sb.AppendLine($"**Status:** {(category.PassRate >= 80 ? "⚠️ Partial" : "❌ Vulnerable")}  ");
                sb.AppendLine($"**Pass Rate:** {category.PassRate:F1}%  ");
                sb.AppendLine($"**Failed Tests:** {category.FailedTests}");
                sb.AppendLine();

                if (category.Findings.Count > 0)
                {
                    sb.AppendLine("| Severity | Finding | Attack |");
                    sb.AppendLine("|----------|---------|--------|");
                    foreach (var finding in category.Findings.Take(5))
                    {
                        sb.AppendLine($"| {finding.Severity} | {finding.Description} | {finding.AttackName} |");
                    }
                    if (category.Findings.Count > 5)
                    {
                        sb.AppendLine($"| ... | *{category.Findings.Count - 5} more findings* | ... |");
                    }
                    sb.AppendLine();
                }
            }
        }

        // Recommendations
        if (Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            sb.AppendLine();
            for (int i = 0; i < Recommendations.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {Recommendations[i]}");
            }
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine("---");
        sb.AppendLine($"*Generated by AgentEval v{AgentEvalVersion} | {FrameworkName} Compliance Report*");

        return sb.ToString();
    }

    /// <inheritdoc />
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
    }

    /// <summary>Save report to file.</summary>
    public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ToJson()
            : ToMarkdown();
        await File.WriteAllTextAsync(filePath, content, cancellationToken);
    }
}

/// <summary>Summary of compliance results.</summary>
public class ComplianceSummary
{
    /// <summary>Total OWASP categories (always 10).</summary>
    public int TotalCategories { get; init; } = 10;

    /// <summary>Categories that were tested.</summary>
    public int TestedCategories { get; init; }

    /// <summary>Categories that passed (100% or above threshold).</summary>
    public int PassedCategories { get; init; }

    /// <summary>Overall pass rate across all tested probes.</summary>
    public double OverallPassRate { get; init; }

    /// <summary>Number of critical severity findings.</summary>
    public int CriticalFindings { get; init; }

    /// <summary>Number of high severity findings.</summary>
    public int HighFindings { get; init; }

    /// <summary>Number of medium severity findings.</summary>
    public int MediumFindings { get; init; }

    /// <summary>Number of low severity findings.</summary>
    public int LowFindings { get; init; }

    /// <summary>Total findings across all categories.</summary>
    public int TotalFindings => CriticalFindings + HighFindings + MediumFindings + LowFindings;
}

/// <summary>Status of a single OWASP LLM category.</summary>
public class OWASPCategoryStatus
{
    /// <summary>OWASP ID (e.g., "LLM01").</summary>
    public required string Id { get; init; }

    /// <summary>Category name (e.g., "Prompt Injection").</summary>
    public required string Name { get; init; }

    /// <summary>Full description of this category.</summary>
    public string Description { get; init; } = "";

    /// <summary>Test status.</summary>
    public CategoryTestStatus Status { get; init; }

    /// <summary>Total tests run for this category.</summary>
    public int TotalTests { get; init; }

    /// <summary>Tests that passed (agent resisted).</summary>
    public int PassedTests { get; init; }

    /// <summary>Tests that failed (agent compromised).</summary>
    public int FailedTests => TotalTests - PassedTests;

    /// <summary>Pass rate percentage.</summary>
    public double PassRate => TotalTests > 0 ? PassedTests * 100.0 / TotalTests : 0;

    /// <summary>Detailed findings for this category.</summary>
    public IReadOnlyList<ComplianceFinding> Findings { get; init; } = [];
}

/// <summary>Test status for a compliance category.</summary>
public enum CategoryTestStatus
{
    /// <summary>Category was tested.</summary>
    Tested,

    /// <summary>Category was not tested.</summary>
    NotTested,

    /// <summary>Category is not applicable (cannot be tested via API probes).</summary>
    NotApplicable
}

/// <summary>Individual finding within a compliance category.</summary>
public class ComplianceFinding
{
    /// <summary>Unique finding ID.</summary>
    public required string Id { get; init; }

    /// <summary>Severity level.</summary>
    public Severity Severity { get; init; }

    /// <summary>Short description.</summary>
    public required string Description { get; init; }

    /// <summary>Attack type that triggered this finding.</summary>
    public required string AttackName { get; init; }

    /// <summary>Probe ID that triggered this finding.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Evidence (if included).</summary>
    public string? Evidence { get; init; }
}
