// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Per-bypass-class attack-success reporting: how a transform class (base64, rot13, homoglyph, a composed
/// chain, or untransformed) performed, with a Wilson interval on each slice.
/// </summary>
/// <param name="BypassClass">
/// The transform chain in apply order (e.g. <c>base64</c>, <c>base64&gt;rot13</c>), or
/// <see cref="Untransformed"/> for seed probes.
/// </param>
/// <param name="Succeeded">Conclusive probes where the attack got through.</param>
/// <param name="Conclusive">Probes that produced a conclusive verdict (Succeeded + Resisted).</param>
/// <param name="Inconclusive">Probes that could not be scored — a coverage gap, excluded from the rate.</param>
/// <param name="SuccessRate">Attack success rate over CONCLUSIVE probes only, with a Wilson 95% interval.</param>
public sealed record BypassClassBreakdown(
    string BypassClass,
    int Succeeded,
    int Conclusive,
    int Inconclusive,
    WilsonInterval SuccessRate)
{
    /// <summary>The class label used for probes that carry no transform suffix.</summary>
    public const string Untransformed = "(none)";

    /// <summary>
    /// Groups probe results by bypass class and computes a Wilson-bounded success rate for each.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> AgentEval generates every bypass family the reference benchmarks use —
    /// 18 codecs plus Expand/Chain composition — but reports results blended into one number. A per-class
    /// blind spot is therefore invisible: if a judge were 0-for-8 on homoglyphs, the aggregate would hide it.
    /// The reference prompt-injection benchmark surfaced exactly that shape of failure in its own detector
    /// (0/8 on five separate classes) only because it sliced the scoreboard by class. More test cases with a
    /// worse scoreboard is not an advantage.</para>
    ///
    /// <para><b>Conclusive-only, per the standing honesty rule.</b> Inconclusive probes are counted and
    /// reported separately but excluded from the denominator: an unscored probe is a coverage gap, not a
    /// resisted one. A class consisting entirely of inconclusive probes reports
    /// <see cref="WilsonInterval.IsMeasured"/> == <see langword="false"/> rather than a 0% success rate.</para>
    ///
    /// <para>The class is read from the probe id suffix that <see cref="TransformProvenance.SuffixId"/>
    /// stamps (<c>PI-001+base64</c>), so no change to the execution pipeline or to
    /// <see cref="ProbeResult"/> is required.</para>
    /// </remarks>
    public static IReadOnlyList<BypassClassBreakdown> FromResults(IEnumerable<ProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results
            .GroupBy(ClassOf, StringComparer.Ordinal)
            .Select(group =>
            {
                var succeeded = group.Count(p => p.Outcome == EvaluationOutcome.Succeeded);
                var resisted = group.Count(p => p.Outcome == EvaluationOutcome.Resisted);
                var inconclusive = group.Count(p => p.Outcome == EvaluationOutcome.Inconclusive);
                var conclusive = succeeded + resisted;

                return new BypassClassBreakdown(
                    group.Key,
                    succeeded,
                    conclusive,
                    inconclusive,
                    WilsonInterval.Compute(succeeded, conclusive));
            })
            .OrderByDescending(b => b.SuccessRate.Estimate)
            .ThenBy(b => b.BypassClass, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Extracts the transform chain from a probe id. <c>PI-001+base64</c> → <c>base64</c>;
    /// <c>PI-001+base64+rot13</c> → <c>base64&gt;rot13</c>; <c>PI-001</c> → <see cref="Untransformed"/>.
    /// </summary>
    private static string ClassOf(ProbeResult result)
    {
        var id = result.ProbeId;
        var firstPlus = id.IndexOf('+', StringComparison.Ordinal);
        if (firstPlus < 0 || firstPlus == id.Length - 1)
        {
            return Untransformed;
        }

        // Chained transforms suffix once per applied transform, so the tail is the chain in apply order.
        return id[(firstPlus + 1)..].Replace('+', '>');
    }

    /// <summary>
    /// Whether this class produced any conclusive evidence. When <see langword="false"/> the class was
    /// exercised but never scored — report it as a coverage gap, not as a clean result.
    /// </summary>
    public bool IsMeasured => SuccessRate.IsMeasured;

    /// <summary>Renders one table row: class, rate with interval, and any unscored probes.</summary>
    public override string ToString() =>
        IsMeasured
            ? $"{BypassClass}: {SuccessRate}" + (Inconclusive > 0 ? $" (+{Inconclusive} inconclusive)" : string.Empty)
            : $"{BypassClass}: not measured ({Inconclusive} inconclusive, 0 conclusive)";
}
