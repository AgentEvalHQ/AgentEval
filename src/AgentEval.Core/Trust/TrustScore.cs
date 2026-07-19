// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Trust;

/// <summary>
/// One signal contributing to a <see cref="TrustScoreResult"/> — a gate verdict (e.g. Allow=1.0/Block=0.0, or
/// a judge's confidence), an eval score, or any other 0..1 measurement the caller wants to fold in.
/// </summary>
/// <param name="Name">Stable identifier for this signal (e.g. a gate's <c>PolicyName</c> or an eval's metric name).</param>
/// <param name="Score">
/// 0..1 — how trustworthy this signal found the subject (1.0 = fully trusted, 0.0 = fully distrusted).
/// Ignored (never read) when <paramref name="Label"/> is <c>"skipped"</c> or <c>"error"</c>.
/// </param>
/// <param name="Weight">Relative weight in the composite. Non-positive weights are excluded, same as <c>WeightedSumAggregation</c>.</param>
/// <param name="Label">
/// <c>"measured"</c> (default) — a real signal, included in the composite. <c>"skipped"</c> or <c>"error"</c> —
/// the SAME vocabulary <see cref="Evals.EvalScore.Label"/> already uses — excludes this signal from the
/// weighted math entirely (not scored at 0.0) so an infrastructure gap never fabricates distrust.
/// </param>
public sealed record TrustSignal(string Name, double Score, double Weight, string Label = "measured");

/// <summary>
/// The composite trust result — a single 0-100 number plus enough detail to see WHY, never a fabricated
/// number when some signals couldn't contribute.
/// </summary>
/// <param name="Score">0-100 composite, or <see langword="null"/> when no signal contributed (nothing to score).</param>
/// <param name="SignalsMeasured">How many of the supplied signals actually contributed to <see cref="Score"/>.</param>
/// <param name="SignalsTotal">Total signals supplied, including excluded ones.</param>
/// <param name="Explanation">Human-readable summary naming excluded signals and why.</param>
public sealed record TrustScoreResult(double? Score, int SignalsMeasured, int SignalsTotal, string Explanation);

/// <summary>
/// Explainability &amp; Trust — a unified, honest composite across gate verdicts and eval scores. The naive
/// approach (average everything, including gaps) is exactly the trap <c>WeightedSumAggregation</c>'s own
/// comment warns against: "including [skipped/error] at 0.0 would incorrectly drag the composite below
/// threshold." <see cref="Compute"/> applies the SAME exclusion discipline already used across this repo's
/// aggregation strategies (<c>WeightedSumAggregation</c>/<c>WeightedMedianAggregation</c>/
/// <c>MinAggregation</c>/<c>MajorityVoteAggregation</c>) to a cross-cutting mix of signal SOURCES, not just
/// sub-evals of one eval tree.
/// </summary>
public static class TrustScoreCalculator
{
    /// <summary>Computes the composite trust score from <paramref name="signals"/>.</summary>
    public static TrustScoreResult Compute(IReadOnlyList<TrustSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var excluded = new List<string>();
        double weightSum = 0, weightedScoreSum = 0;
        var measured = 0;

        foreach (var signal in signals)
        {
            if (signal.Label is "skipped" or "error")
            {
                excluded.Add($"{signal.Name} ({signal.Label})");
                continue;
            }

            if (signal.Weight <= 0)
            {
                excluded.Add($"{signal.Name} (non-positive weight)");
                continue;
            }

            weightSum += signal.Weight;
            weightedScoreSum += Math.Clamp(signal.Score, 0.0, 1.0) * signal.Weight;
            measured++;
        }

        double? score = weightSum > 0 ? Math.Clamp(weightedScoreSum / weightSum * 100.0, 0.0, 100.0) : null;

        var explanation = signals.Count == 0
            ? "No signals supplied — nothing to score."
            : score is null
                ? $"No signal contributed a usable score (0/{signals.Count} measured) — every signal was skipped, errored, or non-positive weight."
                : $"Composite trust score {score:F0}/100 from {measured}/{signals.Count} signal(s) measured" +
                  (excluded.Count > 0 ? $"; excluded: {string.Join(", ", excluded)} (never scored as distrust)." : ".");

        return new TrustScoreResult(score, measured, signals.Count, explanation);
    }
}
