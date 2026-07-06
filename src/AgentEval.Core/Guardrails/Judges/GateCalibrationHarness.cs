// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// "The Bar" of The Tribunal — scores a judge <see cref="IChatGate"/> against a both-directions
/// <see cref="JudgeGoldSet"/> and decides whether it earned the right to block live traffic. A judge should stay
/// in the shadow lane until <see cref="CalibrationReport.IsInlineReady"/> is true; promoting an un-calibrated
/// judge inline ships a fabrication-prone gate, which is worse than none.
/// <para>Reports decisive accuracy, the <b>missed-attack (dangerous-error) count</b> — the number that matters —
/// the false-alarm rate, Cohen's κ vs the gold labels, and (if a baseline is supplied) whether the judge beats a
/// deterministic detector. Re-run it on any model/prompt change and demote a judge that regresses.</para>
/// </summary>
public static class GateCalibrationHarness
{
    /// <summary>Scores <paramref name="judge"/> against <paramref name="goldSet"/> and returns the calibration report.</summary>
    public static async Task<CalibrationReport> EvaluateAsync(
        IChatGate judge, JudgeGoldSet goldSet, CalibrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(goldSet);
        options ??= new CalibrationOptions();

        var judgeResults = await ScoreAsync(judge, goldSet, options.MaxConcurrency, cancellationToken).ConfigureAwait(false);
        var (tp, tn, fp, fn) = Confusion(judgeResults);
        var kappa = Kappa(tp, tn, fp, fn);
        var n = tp + tn + fp + fn;
        var judgeAccuracy = n == 0 ? 0.0 : (double)(tp + tn) / n;
        var benign = fp + tn;
        var fpr = benign == 0 ? 0.0 : (double)fp / benign;

        double? baselineAccuracy = null;
        bool? beatsBaseline = null;
        if (options.DeterministicBaseline is not null)
        {
            var baseResults = await ScoreAsync(options.DeterministicBaseline, goldSet, options.MaxConcurrency, cancellationToken).ConfigureAwait(false);
            var (btp, btn, bfp, bfn) = Confusion(baseResults);
            var bn = btp + btn + bfp + bfn;
            baselineAccuracy = bn == 0 ? 0.0 : (double)(btp + btn) / bn;
            // Beat = strictly more accurate AND no more missed attacks than the deterministic detector.
            beatsBaseline = judgeAccuracy > baselineAccuracy.Value && fn <= bfn;
        }

        var meetsThresholds =
            judgeAccuracy >= options.MinDecisiveAccuracy
            && fn <= options.MaxDangerousErrors
            && fpr <= options.MaxFalsePositiveRate;

        return new CalibrationReport(goldSet.Axis, tp, tn, fp, fn, kappa, baselineAccuracy, beatsBaseline, meetsThresholds, judgeResults);
    }

    private static async Task<IReadOnlyList<CalibrationCaseResult>> ScoreAsync(
        IChatGate gate, JudgeGoldSet goldSet, int maxConcurrency, CancellationToken cancellationToken)
    {
        var results = new CalibrationCaseResult[goldSet.Cases.Count];
        using var throttle = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = goldSet.Cases.Select(async (c, i) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var verdict = await gate.InspectAsync(c.Text, cancellationToken).ConfigureAwait(false);
                results[i] = new CalibrationCaseResult(c.Text, c.ShouldBlock, verdict.Action == GateAction.Block);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static (int tp, int tn, int fp, int fn) Confusion(IReadOnlyList<CalibrationCaseResult> results)
    {
        int tp = 0, tn = 0, fp = 0, fn = 0;
        foreach (var r in results)
        {
            if (r.ShouldBlock && r.Blocked)
            {
                tp++;
            }
            else if (!r.ShouldBlock && !r.Blocked)
            {
                tn++;
            }
            else if (!r.ShouldBlock && r.Blocked)
            {
                fp++;
            }
            else
            {
                fn++;   // should block, but allowed — a missed attack
            }
        }

        return (tp, tn, fp, fn);
    }

    // Cohen's κ for the 2×2 (positive = block) confusion matrix.
    private static double Kappa(int tp, int tn, int fp, int fn)
    {
        double n = tp + tn + fp + fn;
        if (n == 0)
        {
            return 0.0;
        }

        var po = (tp + tn) / n;
        var pYesGold = (tp + fn) / n;
        var pYesJudge = (tp + fp) / n;
        var pNoGold = (tn + fp) / n;
        var pNoJudge = (tn + fn) / n;
        var pe = (pYesGold * pYesJudge) + (pNoGold * pNoJudge);

        if (Math.Abs(1.0 - pe) < 1e-12)
        {
            return po >= 1.0 ? 1.0 : 0.0;   // degenerate: everything one class
        }

        return (po - pe) / (1.0 - pe);
    }
}
