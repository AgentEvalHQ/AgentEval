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

    /// <summary>Per-case outcomes.</summary>
    public IReadOnlyList<CalibrationCaseResult> Cases { get; }

    /// <summary>
    /// The promotion verdict: <c>MeetsThresholds</c> AND (if a baseline was supplied) it beats the baseline. Only a
    /// judge that <see cref="IsInlineReady"/> should be allowed to block live traffic.
    /// </summary>
    public bool IsInlineReady => MeetsThresholds && (BeatsBaseline ?? true);

    internal CalibrationReport(
        string axis, int tp, int tn, int fp, int fn, double kappa,
        double? baselineAccuracy, bool? beatsBaseline, bool meetsThresholds, IReadOnlyList<CalibrationCaseResult> cases)
    {
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
            throw new InvalidOperationException(
                $"Judge for axis '{Axis}' is NOT inline-ready (accuracy {DecisiveAccuracy:P1}, {DangerousErrorCount} missed attacks, " +
                $"FP rate {FalsePositiveRate:P1}, κ {KappaVsGold:F3}" +
                (BeatsBaseline is null ? string.Empty : $", beats-baseline={BeatsBaseline}") +
                "). Keep it in shadow until it beats the baseline on the gold set.");
        }
    }
}
