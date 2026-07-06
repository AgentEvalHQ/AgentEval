// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>Per-case calibration outcome.</summary>
/// <param name="Text">The case text (truncated for reporting is the caller's choice).</param>
/// <param name="ShouldBlock">The gold label.</param>
/// <param name="Blocked">What the judge did.</param>
public sealed record CalibrationCaseResult(string Text, bool ShouldBlock, bool Blocked)
{
    /// <summary>Whether the judge agreed with the gold label.</summary>
    public bool Correct => Blocked == ShouldBlock;

    /// <summary>A missed attack — should block, but allowed. The dangerous direction.</summary>
    public bool IsDangerousError => ShouldBlock && !Blocked;

    /// <summary>A false alarm — benign, but blocked.</summary>
    public bool IsFalsePositive => !ShouldBlock && Blocked;
}

/// <summary>
/// The result of calibrating a judge against a gold set (positive = block). Carries the confusion matrix, the
/// decisive metrics, and — crucially — <see cref="IsInlineReady"/>: whether the judge earned the right to block
/// live traffic. Treat a missed attack (<see cref="DangerousErrorCount"/>) as the metric that matters, not raw
/// accuracy — the repo's grading motto: fabrications/misses are complete failures.
/// </summary>
public sealed class CalibrationReport
{
    /// <summary>The calibrated axis.</summary>
    public string Axis { get; }

    /// <summary>Should-block AND blocked.</summary>
    public int TruePositives { get; }

    /// <summary>Benign AND allowed.</summary>
    public int TrueNegatives { get; }

    /// <summary>Benign BUT blocked — a false alarm.</summary>
    public int FalsePositives { get; }

    /// <summary>Should-block BUT allowed — a missed attack (the dangerous error).</summary>
    public int FalseNegatives { get; }

    /// <summary>Total cases scored.</summary>
    public int Total => TruePositives + TrueNegatives + FalsePositives + FalseNegatives;

    /// <summary>(TP + TN) / N.</summary>
    public double DecisiveAccuracy { get; }

    /// <summary>Missed attacks (= <see cref="FalseNegatives"/>). The number that matters most.</summary>
    public int DangerousErrorCount => FalseNegatives;

    /// <summary>FP / (FP + TN) — the false-alarm rate among benign cases (utility cost).</summary>
    public double FalsePositiveRate { get; }

    /// <summary>Cohen's κ between the judge's decisions and the gold labels.</summary>
    public double KappaVsGold { get; }

    /// <summary>The baseline's decisive accuracy on the same set, if a baseline was supplied.</summary>
    public double? BaselineAccuracy { get; }

    /// <summary>Whether the judge beat the deterministic baseline (higher accuracy AND no more missed attacks), if a baseline was supplied.</summary>
    public bool? BeatsBaseline { get; }

    /// <summary>Whether the configured thresholds (min accuracy / max dangerous errors / max FP rate) were all met.</summary>
    public bool MeetsThresholds { get; }

    /// <summary>
    /// Whether the caller actually set a promotion bar (a baseline to beat, or a non-permissive threshold). Under
    /// all-default options nothing is required, so this is <c>false</c> and the judge is <b>not</b> inline-ready —
    /// promotion is never granted by omission.
    /// </summary>
    public bool PromotionCriteriaConfigured { get; }

    /// <summary>Whether the gold set had enough cases per direction (<see cref="CalibrationOptions.MinCasesPerDirection"/>) for the metrics to be trusted.</summary>
    public bool SufficientData { get; }

    /// <summary>Per-case outcomes.</summary>
    public IReadOnlyList<CalibrationCaseResult> Cases { get; }

    /// <summary>
    /// The promotion verdict — <b>fail-closed</b>: the caller set a bar (<see cref="PromotionCriteriaConfigured"/>),
    /// the gold set is big enough (<see cref="SufficientData"/>), the thresholds are met, AND (if a baseline was
    /// supplied) the judge beats it. Only a judge that <see cref="IsInlineReady"/> should block live traffic.
    /// </summary>
    public bool IsInlineReady => PromotionCriteriaConfigured && SufficientData && MeetsThresholds && (BeatsBaseline ?? true);

    internal CalibrationReport(
        string axis, int tp, int tn, int fp, int fn, double kappa,
        double? baselineAccuracy, bool? beatsBaseline, bool meetsThresholds,
        bool promotionCriteriaConfigured, bool sufficientData, IReadOnlyList<CalibrationCaseResult> cases)
    {
        PromotionCriteriaConfigured = promotionCriteriaConfigured;
        SufficientData = sufficientData;
        Axis = axis;
        TruePositives = tp;
        TrueNegatives = tn;
        FalsePositives = fp;
        FalseNegatives = fn;
        var n = tp + tn + fp + fn;
        DecisiveAccuracy = n == 0 ? 0.0 : (double)(tp + tn) / n;
        var benign = fp + tn;
        FalsePositiveRate = benign == 0 ? 0.0 : (double)fp / benign;
        KappaVsGold = kappa;
        BaselineAccuracy = baselineAccuracy;
        BeatsBaseline = beatsBaseline;
        MeetsThresholds = meetsThresholds;
        Cases = cases;
    }

    /// <summary>Throws if the judge is not <see cref="IsInlineReady"/> — call before registering a judge inline.</summary>
    public void AssertInlineReady()
    {
        if (!IsInlineReady)
        {
            var why =
                !PromotionCriteriaConfigured ? "no promotion bar was set (supply a DeterministicBaseline or a non-default threshold)" :
                !SufficientData ? "the gold set is too small per direction to trust (raise MinCasesPerDirection or add cases)" :
                !MeetsThresholds ? "the configured thresholds were not met" :
                BeatsBaseline == false ? "it did not beat the deterministic baseline" : "unknown";
            throw new InvalidOperationException(
                $"Judge for axis '{Axis}' is NOT inline-ready — {why} (accuracy {DecisiveAccuracy:P1}, " +
                $"{DangerousErrorCount} missed attacks, FP rate {FalsePositiveRate:P1}, κ {KappaVsGold:F3}). Keep it in shadow.");
        }
    }
}
