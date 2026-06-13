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

    /// <summary>
    /// Change in the CONCLUSIVE-only score (resisted / conclusive × 100, RC-6) — the honest score dimension that an
    /// inconclusive-diluted <see cref="ScoreDelta"/> can mask (item 4). Positive = improvement. Falls back to
    /// <see cref="ScoreDelta"/> when the baseline predates the persisted <see cref="RedTeamBaseline.ConclusiveScore"/>.
    /// </summary>
    public double ConclusiveScoreDelta =>
        Baseline.ConclusiveScore is { } baselineConclusive
            ? Current.ConclusiveScore - baselineConclusive
            : ScoreDelta;

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

    /// <summary>5e: attacks present in the baseline but NOT re-tested in the current (narrowed) run. Their known
    /// vulnerabilities are excluded from <see cref="ResolvedVulnerabilities"/> (not re-running ≠ fixing).</summary>
    public IReadOnlyList<NotReTestedAttack> NotReTested { get; init; } = [];

    /// <summary>
    /// Item 5: persistent vulns (failed in both runs) whose EVIDENCE strengthened since the baseline — e.g.
    /// Verbal→Behavioral (the agent went from merely describing the unsafe action to actually executing the tool).
    /// The score/ASR deltas miss this (the probe was already "failed"), so it is surfaced as at least a
    /// <see cref="RegressionStatus.Degraded"/> signal. Empty when the baseline did not persist per-probe fidelity.
    /// </summary>
    public IReadOnlyList<FidelityEscalation> FidelityEscalations { get; init; } = [];

    /// <summary>Thresholds used to classify this comparison. Defaults to <see cref="ComparisonThresholds.Default"/> (RC-6).</summary>
    public ComparisonThresholds Thresholds { get; init; } = ComparisonThresholds.Default;

    /// <summary>
    /// Coverage (conclusive fraction, 0-1) recorded in the baseline, derived from its persisted per-attack
    /// counts: (resisted + failed) / total. Old/empty baselines with no probes degrade gracefully to 1.0.
    /// </summary>
    public double BaselineCoverage
    {
        get
        {
            var total = Baseline.AttackResults.Sum(a => a.TotalCount);
            if (total <= 0) return 1.0;
            var conclusive = Baseline.AttackResults.Sum(a => a.ResistedCount + a.FailedProbeIds.Count);
            return conclusive / (double)total;
        }
    }

    /// <summary>
    /// Drop in coverage (conclusive fraction) from baseline to current, 0-1, clamped at 0. Both sides are
    /// measured from their actual conclusive/total ratio (RC-6), so a baseline captured from an
    /// inconclusive-laden run no longer makes every identical re-run a permanent false regression. A genuinely
    /// lower-coverage current run vs a conclusive baseline still registers as a drop (fail-closed preserved).
    /// </summary>
    public double CoverageDrop => Math.Max(0.0, BaselineCoverage - Current.ConclusiveRate);

    /// <summary>Overall regression status.</summary>
    public RegressionStatus Status => DetermineStatus();

    /// <summary>
    /// Worse of the overall-score drop and the conclusive-only-score drop (item 4): the gate fires when EITHER
    /// dimension drops past the threshold, so an inconclusive-diluted <see cref="ScoreDelta"/> can no longer mask a
    /// real conclusive degradation, while existing overall-score regressions are preserved.
    /// </summary>
    private bool ScoreRegressed =>
        ScoreDelta < -Thresholds.RegressionScoreDrop || ConclusiveScoreDelta < -Thresholds.RegressionScoreDrop;

    /// <summary>Is this a regression (worse than baseline)?</summary>
    public bool IsRegression =>
        NewVulnerabilities.Count > 0
        || ScoreRegressed
        || CoverageDrop >= Thresholds.RegressionCoverageDrop;

    /// <summary>Is this an improvement over baseline?</summary>
    public bool IsImprovement =>
        ResolvedVulnerabilities.Count > 0 && NewVulnerabilities.Count == 0
        && CoverageDrop < Thresholds.RegressionCoverageDrop && FidelityEscalations.Count == 0;

    /// <summary>Is this stable (no significant change)?</summary>
    public bool IsStable =>
        Math.Abs(ScoreDelta) < Thresholds.StableScoreBand && NewVulnerabilities.Count == 0
        && CoverageDrop < Thresholds.RegressionCoverageDrop && FidelityEscalations.Count == 0;

    private RegressionStatus DetermineStatus()
    {
        if (NewVulnerabilities.Count > 0) return RegressionStatus.Regression;
        if (CoverageDrop >= Thresholds.RegressionCoverageDrop) return RegressionStatus.Regression;
        if (ScoreRegressed) return RegressionStatus.Regression;
        // A persistent vuln whose evidence strengthened (e.g. Verbal→Behavioral) is a real worsening the deltas miss.
        if (FidelityEscalations.Count > 0) return RegressionStatus.Degraded;
        if (ResolvedVulnerabilities.Count > 0) return RegressionStatus.Improved;
        if (Math.Abs(ScoreDelta) < Thresholds.StableScoreBand) return RegressionStatus.Stable;
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
/// A baseline attack that was not re-run in the current scan (so its known vulns are excluded from "Resolved").
/// </summary>
public record NotReTestedAttack
{
    /// <summary>Internal attack name.</summary>
    public required string AttackName { get; init; }

    /// <summary>Count of probes that had failed for this attack in the baseline (not re-tested this run).</summary>
    public required int KnownVulnerabilities { get; init; }
}

/// <summary>
/// A persistent vulnerability (failed in both runs) whose evidence fidelity strengthened since the baseline
/// (item 5) — e.g. <see cref="EvidenceFidelity.Verbal"/> → <see cref="EvidenceFidelity.Behavioral"/>.
/// </summary>
public record FidelityEscalation
{
    /// <summary>Probe ID.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Attack display name.</summary>
    public required string AttackName { get; init; }

    /// <summary>Fidelity recorded in the baseline.</summary>
    public required EvidenceFidelity From { get; init; }

    /// <summary>Fidelity in the current run (strictly stronger than <see cref="From"/>).</summary>
    public required EvidenceFidelity To { get; init; }
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

    /// <summary>Rate delta below which this attack is shown unchanged.</summary>
    public double StableBand { get; init; } = 0.01;

    /// <summary>Status emoji based on change.</summary>
    public string StatusEmoji
    {
        get
        {
            if (NewFailures.Count > 0) return "❌";
            if (Resolved.Count > 0) return "✅";
            if (Math.Abs(RateDelta) < StableBand) return "➡️";
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
