// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Reporting.Compliance;

/// <summary>
/// MITRE ATLAS compliance report generated from red team scan results.
/// </summary>
public class MITREATLASReport : IComplianceReport
{
    /// <inheritdoc />
    public string FrameworkName => "MITRE ATLAS";

    /// <inheritdoc />
    public string FrameworkVersion { get; init; } = "4.5";

    /// <inheritdoc />
    public required string AgentName { get; init; }

    /// <inheritdoc />
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Duration of the original scan.</summary>
    public TimeSpan ScanDuration { get; init; }

    /// <summary>AgentEval version used.</summary>
    public string AgentEvalVersion { get; init; } = "0.2.0";

    /// <summary>Status for each MITRE ATLAS technique.</summary>
    public required IReadOnlyList<MITRETechniqueStatus> Techniques { get; init; }

    /// <summary>Tactic-level coverage summary.</summary>
    public required IReadOnlyList<TacticCoverage> Tactics { get; init; }

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

    /// <summary>Number of techniques tested.</summary>
    public int TestedCount => Techniques.Count(t => t.Status == TechniqueTestStatus.Tested);

    /// <summary>Number of techniques not applicable.</summary>
    public int NotApplicableCount => Techniques.Count(t => t.Status == TechniqueTestStatus.NotApplicable);

    /// <summary>Number of techniques not tested.</summary>
    public int NotTestedCount => Techniques.Count(t => t.Status == TechniqueTestStatus.NotTested);

    // === Export Methods ===

    /// <inheritdoc />
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# MITRE ATLAS Compliance Report");
        sb.AppendLine();
        sb.AppendLine($"**Agent:** {AgentName}  ");
        sb.AppendLine($"**Date:** {GeneratedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Framework:** {FrameworkName} v{FrameworkVersion}  ");
        sb.AppendLine($"**Scan Duration:** {ScanDuration.TotalSeconds:F1}s");
        sb.AppendLine();

        // Executive Summary
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Techniques Tested | {TestedCount}/{Techniques.Count} |");
        sb.AppendLine($"| Tactics Covered | {Tactics.Count(t => t.TestedCount > 0)}/{Tactics.Count} |");
        sb.AppendLine($"| Overall Pass Rate | {Summary.OverallPassRate:F1}% |");
        sb.AppendLine($"| Risk Level | {RiskLevel} |");
        sb.AppendLine();

        // Technique Coverage Table
        sb.AppendLine("## Technique Coverage");
        sb.AppendLine();
        sb.AppendLine("| ID | Name | Tactic | Status | Tests | Passed | Pass Rate |");
        sb.AppendLine("|----|------|--------|--------|-------|--------|-----------|");

        foreach (var technique in Techniques)
        {
            var statusIcon = technique.Status switch
            {
                TechniqueTestStatus.Tested when technique.PassRate >= 80 => "✅",
                TechniqueTestStatus.Tested => "⚠️",
                TechniqueTestStatus.NotTested => "❌",
                TechniqueTestStatus.NotApplicable => "⬜",
                _ => "❓"
            };

            var passRateStr = technique.Status == TechniqueTestStatus.Tested
                ? $"{technique.PassRate:F0}%"
                : "-";
            var testsStr = technique.Status == TechniqueTestStatus.Tested
                ? technique.TotalTests.ToString()
                : "-";
            var passedStr = technique.Status == TechniqueTestStatus.Tested
                ? technique.PassedTests.ToString()
                : "-";

            sb.AppendLine($"| {technique.Id} | {statusIcon} {technique.Name} | {technique.TacticName} | {technique.Status} | {testsStr} | {passedStr} | {passRateStr} |");
        }
        sb.AppendLine();

        // Tactic Coverage Section
        sb.AppendLine("## Tactic Coverage");
        sb.AppendLine();
        sb.AppendLine("| Tactic | Techniques | Tested | Coverage |");
        sb.AppendLine("|--------|------------|--------|----------|");

        foreach (var tactic in Tactics)
        {
            var coveragePct = tactic.TotalCount > 0 ? (tactic.TestedCount * 100 / tactic.TotalCount) : 0;
            sb.AppendLine($"| {tactic.Name} | {tactic.TotalCount} | {tactic.TestedCount} | {coveragePct}% |");
        }
        sb.AppendLine();

        // Detailed Findings (if any failed)
        var failedTechniques = Techniques.Where(t => t.Status == TechniqueTestStatus.Tested && t.PassRate < 100).ToList();
        if (failedTechniques.Count > 0)
        {
            sb.AppendLine("## Technique Details");
            sb.AppendLine();

            foreach (var technique in failedTechniques)
            {
                sb.AppendLine($"### {technique.Id}: {technique.Name}");
                sb.AppendLine();
                sb.AppendLine($"**MITRE Reference:** https://atlas.mitre.org/techniques/{technique.Id}  ");
                sb.AppendLine($"**Status:** {(technique.PassRate >= 80 ? "⚠️ Partial" : "❌ Vulnerable")}  ");
                sb.AppendLine($"**Pass Rate:** {technique.PassRate:F1}%  ");
                sb.AppendLine($"**Failed Tests:** {technique.FailedTests}");
                sb.AppendLine();

                if (technique.Findings.Count > 0)
                {
                    sb.AppendLine("| Severity | Probe ID | Result |");
                    sb.AppendLine("|----------|----------|--------|");
                    foreach (var finding in technique.Findings.Take(5))
                    {
                        sb.AppendLine($"| {finding.Severity} | {finding.ProbeId} | ❌ FAIL |");
                    }
                    if (technique.Findings.Count > 5)
                    {
                        sb.AppendLine($"| ... | *{technique.Findings.Count - 5} more* | ... |");
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

/// <summary>Status of a single MITRE ATLAS technique.</summary>
public class MITRETechniqueStatus
{
    /// <summary>Technique ID (e.g., "AML.T0051").</summary>
    public required string Id { get; init; }

    /// <summary>Technique name (e.g., "LLM Prompt Injection").</summary>
    public required string Name { get; init; }

    /// <summary>Description of this technique.</summary>
    public string Description { get; init; } = "";

    /// <summary>Associated tactic ID.</summary>
    public required string TacticId { get; init; }

    /// <summary>Associated tactic name.</summary>
    public required string TacticName { get; init; }

    /// <summary>Test status.</summary>
    public TechniqueTestStatus Status { get; init; }

    /// <summary>Total tests run for this technique.</summary>
    public int TotalTests { get; init; }

    /// <summary>Tests that passed (agent resisted).</summary>
    public int PassedTests { get; init; }

    /// <summary>Tests that failed (agent compromised).</summary>
    public int FailedTests => TotalTests - PassedTests;

    /// <summary>Pass rate percentage.</summary>
    public double PassRate => TotalTests > 0 ? PassedTests * 100.0 / TotalTests : 0;

    /// <summary>Detailed findings for this technique.</summary>
    public IReadOnlyList<ComplianceFinding> Findings { get; init; } = [];

    /// <summary>MITRE ATLAS URL reference.</summary>
    public string ReferenceUrl => $"https://atlas.mitre.org/techniques/{Id}";
}

/// <summary>Test status for a MITRE ATLAS technique.</summary>
public enum TechniqueTestStatus
{
    /// <summary>Technique was tested.</summary>
    Tested,

    /// <summary>Technique was not tested.</summary>
    NotTested,

    /// <summary>Technique is not applicable.</summary>
    NotApplicable
}

/// <summary>Coverage summary for a MITRE ATLAS tactic.</summary>
public class TacticCoverage
{
    /// <summary>Tactic ID.</summary>
    public required string Id { get; init; }

    /// <summary>Tactic name.</summary>
    public required string Name { get; init; }

    /// <summary>Total techniques in this tactic.</summary>
    public int TotalCount { get; init; }

    /// <summary>Techniques that were tested.</summary>
    public int TestedCount { get; init; }

    /// <summary>Techniques that passed (100%).</summary>
    public int PassedCount { get; init; }

    /// <summary>Coverage percentage.</summary>
    public double CoveragePercent => TotalCount > 0 ? TestedCount * 100.0 / TotalCount : 0;
}
