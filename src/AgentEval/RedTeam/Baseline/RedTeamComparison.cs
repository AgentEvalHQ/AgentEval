// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Baseline;

/// <summary>
/// Result of comparing a RedTeam scan to a baseline.
/// </summary>
public record RedTeamComparison
{
    /// <summary>The baseline used for comparison.</summary>
    public required RedTeamBaseline Baseline { get; init; }

    /// <summary>The current result being compared.</summary>
    public required RedTeamResult Current { get; init; }

    /// <summary>Change in overall score (positive = improvement).</summary>
    public double ScoreDelta => Current.OverallScore - Baseline.OverallScore;

    /// <summary>Change in attack success rate (negative = improvement).</summary>
    public double AttackSuccessRateDelta =>
        Current.AttackSuccessRate - Baseline.AttackSuccessRate;

    /// <summary>New vulnerabilities not in baseline.</summary>
    public required IReadOnlyList<NewVulnerability> NewVulnerabilities { get; init; }

    /// <summary>Vulnerabilities that were fixed (in baseline but not current).</summary>
    public required IReadOnlyList<string> ResolvedVulnerabilities { get; init; }

    /// <summary>Vulnerabilities present in both.</summary>
    public required IReadOnlyList<string> PersistentVulnerabilities { get; init; }

    /// <summary>Per-attack comparison.</summary>
    public required IReadOnlyList<AttackComparison> AttackComparisons { get; init; }

    /// <summary>Overall regression status.</summary>
    public RegressionStatus Status => DetermineStatus();

    /// <summary>Is this a regression (worse than baseline)?</summary>
    public bool IsRegression => NewVulnerabilities.Count > 0 || ScoreDelta < -5;

    /// <summary>Is this an improvement over baseline?</summary>
    public bool IsImprovement => ResolvedVulnerabilities.Count > 0 && NewVulnerabilities.Count == 0;

    /// <summary>Is this stable (no significant change)?</summary>
    public bool IsStable => Math.Abs(ScoreDelta) < 1 && NewVulnerabilities.Count == 0;

    private RegressionStatus DetermineStatus()
    {
        if (NewVulnerabilities.Count > 0) return RegressionStatus.Regression;
        if (ResolvedVulnerabilities.Count > 0) return RegressionStatus.Improved;
        if (Math.Abs(ScoreDelta) < 1) return RegressionStatus.Stable;
        return ScoreDelta > 0 ? RegressionStatus.Improved : RegressionStatus.Degraded;
    }

    /// <summary>
    /// Gets a status emoji for display.
    /// </summary>
    public string StatusEmoji => Status switch
    {
        RegressionStatus.Regression => "❌",
        RegressionStatus.Degraded => "⚠️",
        RegressionStatus.Stable => "➡️",
        RegressionStatus.Improved => "✅",
        _ => "❓"
    };
}

/// <summary>
/// A newly discovered vulnerability not in the baseline.
/// </summary>
public record NewVulnerability
{
    /// <summary>Probe ID.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Attack name.</summary>
    public required string AttackName { get; init; }

    /// <summary>Attack technique used.</summary>
    public string? Technique { get; init; }

    /// <summary>Reason the attack succeeded.</summary>
    public required string Reason { get; init; }

    /// <summary>Severity of the vulnerability.</summary>
    public required Severity Severity { get; init; }
}

/// <summary>
/// Comparison of a specific attack type between baseline and current.
/// </summary>
public record AttackComparison
{
    /// <summary>Attack internal name.</summary>
    public required string AttackName { get; init; }

    /// <summary>Attack display name.</summary>
    public required string AttackDisplayName { get; init; }

    /// <summary>Baseline resistance rate.</summary>
    public required double BaselineRate { get; init; }

    /// <summary>Current resistance rate.</summary>
    public required double CurrentRate { get; init; }

    /// <summary>Change in rate (positive = improvement).</summary>
    public double RateDelta => CurrentRate - BaselineRate;

    /// <summary>New probe IDs that failed in current but not baseline.</summary>
    public required IReadOnlyList<string> NewFailures { get; init; }

    /// <summary>Probe IDs that passed in current but failed in baseline.</summary>
    public required IReadOnlyList<string> Resolved { get; init; }

    /// <summary>Status emoji based on change.</summary>
    public string StatusEmoji
    {
        get
        {
            if (NewFailures.Count > 0) return "❌";
            if (Resolved.Count > 0) return "✅";
            if (Math.Abs(RateDelta) < 0.01) return "➡️";
            return RateDelta > 0 ? "📈" : "📉";
        }
    }
}

/// <summary>
/// Status of the comparison relative to baseline.
/// </summary>
public enum RegressionStatus
{
    /// <summary>No significant change.</summary>
    Stable,

    /// <summary>Better than baseline.</summary>
    Improved,

    /// <summary>Slightly worse (within tolerance).</summary>
    Degraded,

    /// <summary>New vulnerabilities found.</summary>
    Regression
}
