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

    /// <summary>
    /// Change in overall score (positive = improvement). <see langword="null"/> when either run has no overall score — it
    /// measured nothing, so the comparison is refused (<see cref="IsComparable"/>, T81).
    /// </summary>
    public double? ScoreDelta => Current.OverallScore - Baseline.OverallScore;

    /// <summary>
    /// Change in the CONCLUSIVE-only score (resisted / conclusive × 100, RC-6) — the honest score dimension that an
    /// inconclusive-diluted <see cref="ScoreDelta"/> can mask (item 4). Positive = improvement. Falls back to
    /// <see cref="ScoreDelta"/> when the baseline has no <see cref="RedTeamBaseline.ConclusiveScore"/> (it predates the
    /// field). <see langword="null"/> when the current run has no conclusive score.
    /// </summary>
    public double? ConclusiveScoreDelta =>
        Baseline.ConclusiveScore is { } baselineConclusive
            ? Current.ConclusiveScore - baselineConclusive
            : ScoreDelta;

    /// <summary>Change in attack success rate (negative = improvement); <see langword="null"/> when either run has none.</summary>
    public double? AttackSuccessRateDelta =>
        Current.AttackSuccessRate - Baseline.AttackSuccessRate;

    /// <summary>New vulnerabilities not in baseline.</summary>
    public required IReadOnlyList<NewVulnerability> NewVulnerabilities { get; init; }

    /// <summary>Vulnerabilities that were fixed (in baseline but not current).</summary>
    public required IReadOnlyList<string> ResolvedVulnerabilities { get; init; }

    /// <summary>Vulnerabilities present in both.</summary>
    public required IReadOnlyList<string> PersistentVulnerabilities { get; init; }

    /// <summary>Known vulnerabilities in retested attacks whose probes produced no conclusive verdict this run.</summary>
    public IReadOnlyList<string> UnverifiedVulnerabilities { get; init; } = [];

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

    /// <summary>
    /// ADR-021 (B.1): true when the baseline and the current run were graded under different <see cref="JudgeMode"/>s.
    /// The probe-set diff stays meaningful, but the <see cref="FidelityEscalations"/> signal is suppressed (a
    /// judge-primary-vs-fallback fidelity shift is a grading artefact, not a regression). Surface it so the operator
    /// knows to re-save the baseline under the current mode.
    /// </summary>
    public bool GradingModeMismatch { get; init; }

    /// <summary>Thresholds used to classify this comparison. Defaults to <see cref="ComparisonThresholds.Default"/> (RC-6).</summary>
    public ComparisonThresholds Thresholds { get; init; } = ComparisonThresholds.Default;

    /// <summary>
    /// Coverage (conclusive fraction, 0-1) recorded in the baseline, derived from its persisted per-attack
    /// counts: (resisted + failed) / total, or its top-level counts. Null when counts were not recorded.
    /// </summary>
    public double? BaselineCoverage
    {
        get
        {
            var total = Baseline.AttackResults.Sum(a => a.TotalCount);
            if (total <= 0)
                return Baseline.TotalProbes > 0 && Baseline.ConclusiveProbes is { } count
                    ? count / (double)Baseline.TotalProbes : null;
            var conclusive = Baseline.AttackResults.Sum(a => a.ConclusiveCount);
            return conclusive / (double)total;
        }
    }

    /// <summary>
    /// Drop in coverage (conclusive fraction) from baseline to current, 0-1, clamped at 0. Both sides are
    /// measured from their actual conclusive/total ratio (RC-6), so a baseline captured from an
    /// inconclusive-laden run no longer makes every identical re-run a permanent false regression. A genuinely
    /// lower-coverage current run vs a conclusive baseline still registers as a drop (fail-closed preserved).
    /// </summary>
    public double? CoverageDrop => BaselineCoverage is { } baseline && Current.ConclusiveRate is { } current
        ? Math.Max(0.0, baseline - current) : null;

    /// <summary>
    /// Probes of the baseline that reached a verdict: <see cref="RedTeamBaseline.ConclusiveProbes"/>, or, for a
    /// baseline saved before that field existed, its persisted per-attack resisted and failed counts.
    /// </summary>
    private int BaselineConclusiveProbes =>
        Baseline.ConclusiveProbes
        ?? Baseline.AttackResults.Sum(a => a.ConclusiveCount);

    /// <summary>
    /// Whether both runs have scores and coverage, and every known vulnerability in a retested attack has a new verdict.
    /// When either side measured nothing, it has no scores, so <see cref="ScoreDelta"/>, <see cref="ConclusiveScoreDelta"/>
    /// and <see cref="AttackSuccessRateDelta"/> are <see langword="null"/>; <see cref="NotComparableReason"/> says why, and the
    /// comparison is refused: its <see cref="Status"/> is <see cref="RegressionStatus.Incomparable"/>, neither a
    /// regression nor a pass (<see cref="IsRegression"/>, <see cref="IsStable"/> and <see cref="IsImprovement"/> are all
    /// false), and the CLI exits 13 (incomparable). Reading it as a regression said the agent got worse when one run had
    /// measured nothing.
    /// </summary>
    public bool IsComparable => NotComparableReason is null;

    /// <summary>Why the two runs cannot be compared, or <c>null</c> when <see cref="IsComparable"/>.</summary>
    public string? NotComparableReason =>
        (BaselineConclusiveProbes, Current.ConclusiveProbes) switch
        {
            (0, 0) => "neither the baseline nor the current run has a probe that reached a verdict",
            (0, _) => $"the baseline ({Baseline.Version}) has no probe that reached a verdict",
            (_, 0) => "the current run has no probe that reached a verdict",
            // A score left out of a baseline file (or a run with verdicts but no probe count) has no delta to read (T81).
            _ when UnverifiedVulnerabilities.Count > 0 => $"{UnverifiedVulnerabilities.Count} known vulnerability probe(s) are unverified in this run; re-run them to compare",
            _ when BaselineCoverage is null => $"the baseline ({Baseline.Version}) records no probe counts, so its coverage was not measured",
            _ when Baseline.OverallScore is null => $"the baseline ({Baseline.Version}) records no overall score",
            _ when Current.OverallScore is null => "the current run has no overall score",
            _ => null,
        };

    /// <summary>Overall regression status.</summary>
    public RegressionStatus Status => DetermineStatus();

    /// <summary>
    /// Worse of the overall-score drop and the conclusive-only-score drop (item 4): the gate fires when EITHER
    /// dimension drops past the threshold, so an inconclusive-diluted <see cref="ScoreDelta"/> can no longer mask a
    /// real conclusive degradation, while existing overall-score regressions are preserved.
    /// </summary>
    private bool ScoreRegressed =>
        ScoreDelta < -Thresholds.RegressionScoreDrop || ConclusiveScoreDelta < -Thresholds.RegressionScoreDrop;

    /// <summary>
    /// Is this a regression (worse than baseline)? False when the runs are not comparable: that comparison is refused
    /// (<see cref="RegressionStatus.Incomparable"/>), not decided either way.
    /// </summary>
    public bool IsRegression =>
        IsComparable
        && (NewVulnerabilities.Count > 0
            || ScoreRegressed
            || CoverageDrop >= Thresholds.RegressionCoverageDrop);

    /// <summary>Is this an improvement over baseline?</summary>
    public bool IsImprovement =>
        IsComparable
        && ResolvedVulnerabilities.Count > 0 && NewVulnerabilities.Count == 0
        && CoverageDrop < Thresholds.RegressionCoverageDrop && FidelityEscalations.Count == 0;

    /// <summary>Is this stable (no significant change)?</summary>
    public bool IsStable =>
        IsComparable
        && ScoreDelta is { } delta && Math.Abs(delta) < Thresholds.StableScoreBand && NewVulnerabilities.Count == 0
        && CoverageDrop < Thresholds.RegressionCoverageDrop && FidelityEscalations.Count == 0;

    private RegressionStatus DetermineStatus()
    {
        // A side that measured nothing cannot show the other regressed or did not (T13-10): the comparison is refused.
        // First, because against a baseline that measured nothing every current failure would read as new.
        if (!IsComparable) return RegressionStatus.Incomparable;
        if (NewVulnerabilities.Count > 0) return RegressionStatus.Regression;
        if (CoverageDrop >= Thresholds.RegressionCoverageDrop) return RegressionStatus.Regression;
        if (ScoreRegressed) return RegressionStatus.Regression;
        // A persistent vuln whose evidence strengthened (e.g. Verbal→Behavioral) is a real worsening the deltas miss.
        if (FidelityEscalations.Count > 0) return RegressionStatus.Degraded;
        if (ResolvedVulnerabilities.Count > 0) return RegressionStatus.Improved;
        // IsComparable means both runs have an overall score; the guard keeps a missing one from reading as a change.
        if (ScoreDelta is not { } delta) return RegressionStatus.Incomparable;
        if (Math.Abs(delta) < Thresholds.StableScoreBand) return RegressionStatus.Stable;
        return delta > 0 ? RegressionStatus.Improved : RegressionStatus.Degraded;
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
        _ => "❓"   // Incomparable: refused, neither better nor worse
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
    public required double? BaselineRate { get; init; }

    /// <summary>Current resistance rate.</summary>
    public required double? CurrentRate { get; init; }

    /// <summary>Change in rate (positive = improvement).</summary>
    public double? RateDelta => CurrentRate - BaselineRate;

    /// <summary>True when the baseline never tested this attack.</summary>
    public bool IsNewAttack { get; init; }

    /// <summary>Why no rate delta can be measured, or null when both rates exist.</summary>
    public string? RateNotComparableReason => IsNewAttack ? "the attack is not in the baseline"
        : BaselineRate is null ? "none of its baseline probes reached a verdict"
        : CurrentRate is null ? "none of its probes reached a verdict in this run" : null;

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
            if (RateDelta is not { } delta) return "❓";
            if (Math.Abs(delta) < StableBand) return "➡️";
            return delta > 0 ? "📈" : "📉";
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
    Regression,

    /// <summary>
    /// The runs cannot be compared: one of them has no probe that reached a verdict
    /// (<see cref="RedTeamComparison.NotComparableReason"/>). The comparison is refused: neither a regression nor a pass.
    /// </summary>
    Incomparable
}
