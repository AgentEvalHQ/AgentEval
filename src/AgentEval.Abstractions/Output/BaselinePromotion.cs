// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

using System.Globalization;

/// <summary>
/// The rule a run must clear before it can become a subject's baseline.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-031 §5.3 and §10:</b> <i>"If a VOID run can be promoted to a baseline, or renders like a
/// FAIL, the distinction is decorative."</i> A baseline is the thing every later run is measured
/// against, so promoting a run that measured nothing does not merely record a bad number — it makes
/// every subsequent comparison meaningless while looking exactly like a healthy one.
/// </para>
/// <para>
/// ⚠ <b>THE THREE CONDITIONS ARE DECOMPOSED ON PURPOSE, because they are not the same failure.</b>
/// A pooled "the run is no good" check would hide which one you have. <see cref="RefusalFor"/>
/// names the one that fired.
/// </para>
/// <list type="number">
/// <item><b>A declared VOID verdict.</b> ⚠ <b>Not reachable today, and that is measured rather
/// than assumed:</b> <c>summary.schema.json</c> constrains <c>verdict</c> to the enum
/// <c>PASS | FAIL | WARN | PENDING</c>, so nothing in this repository can currently write a VOID
/// run summary that <c>doctor</c> would accept. Widening that enum is ADR-031 S4, which is gated on
/// Q5, and the schema budget it would spend is ADR-030 §6.2's single change, gated on Q4. This
/// clause is therefore a PRECONDITION for S4 rather than a live check — but it has to exist before
/// VOID ships, not after, or the first VOID run gets promoted by a store that never heard of it.</item>
/// <item><b>Nothing was measured at all</b> (<c>Stats.Total == 0</c>). <b>Reachable today.</b> This
/// is the green-because-nothing-ran shape: a verdict of PASS over zero scenarios is not a pass, it
/// is an absence, and an absence promoted to a baseline is a bar of zero that every later run
/// clears.</item>
/// <item><b>Everything that ran was skipped</b> (<c>Skipped == Total</c>, <c>Total &gt; 0</c>).
/// <b>Reachable today.</b> ADR-030 Slice 0.1 already ruled that an all-skipped composite reports
/// <c>label:"skipped"</c> rather than a pass; the same reasoning applies one level up.</item>
/// </list>
/// <para>
/// ⚠ <b>It refuses, it does not repair.</b> Downgrading the verdict or promoting anyway with a
/// warning both end with a baseline on disk that nobody chose.
/// </para>
/// </remarks>
public static class BaselinePromotion
{
    /// <summary>The verdict a run carries when its own controls make it inadmissible. ADR-031 S4.</summary>
    public const string VoidVerdict = "VOID";

    /// <summary>
    /// Names why <paramref name="summary"/> cannot be promoted to a baseline, or
    /// <see langword="null"/> when it can.
    /// </summary>
    /// <param name="summary">The candidate run summary.</param>
    /// <returns>A one-sentence reason, or <see langword="null"/>.</returns>
    public static string? RefusalFor(RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // Trimmed and case-insensitive: refusing MORE than the exact literal is the safe direction,
        // and "void" from a hand-edited file is the same fact as "VOID" from a writer.
        if (string.Equals(summary.Verdict?.Trim(), VoidVerdict, StringComparison.OrdinalIgnoreCase))
        {
            return "the run's verdict is VOID — its own controls made it inadmissible, and a baseline "
                + "taken from an inadmissible run makes the VOID distinction decorative";
        }

        if (summary.Stats is null)
        {
            return "the run carries no stats block, so nobody can say whether anything was measured";
        }

        if (summary.Stats.Total == 0)
        {
            return "the run measured NOTHING (0 scenarios), so its verdict describes an absence rather "
                + "than a result — promoting it sets a bar every later run clears by default";
        }

        if (summary.Stats.Skipped >= summary.Stats.Total)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"every one of the run's {summary.Stats.Total} scenarios was SKIPPED, so there is no measurement in it to be a baseline");
        }

        return null;
    }

    /// <summary>True when <paramref name="summary"/> may become a baseline.</summary>
    /// <param name="summary">The candidate run summary.</param>
    /// <returns>Whether it is promotable.</returns>
    public static bool IsPromotable(RunSummary summary) => RefusalFor(summary) is null;

    /// <summary>
    /// Refuses <paramref name="summary"/> unless it may become a baseline.
    /// </summary>
    /// <param name="summary">The candidate run summary.</param>
    /// <exception cref="InvalidOperationException">The run is not promotable.</exception>
    /// <remarks>
    /// Every <see cref="IOutputStore"/> implementation calls this, including the null one. A rule
    /// that holds in some stores and not others is not a rule about baselines, it is a rule about
    /// which store you happened to configure.
    /// </remarks>
    public static void EnsurePromotable(RunSummary summary)
    {
        if (RefusalFor(summary) is { } reason)
        {
            throw new InvalidOperationException(
                $"This run cannot be promoted to a baseline: {reason}. (ADR-031 §5.3.)");
        }
    }
}
