// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Graders;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>What the design's pre-registered decision rule came out at on this run.</summary>
/// <remarks>
/// Three values, and the third is the important one. A rule whose only outcomes are MET and NOT MET
/// has nowhere to put "the comparison this rule names was never run", so an unrunnable rule
/// disappears from the report instead of appearing as a stated absence — which is how the
/// <c>≥ 10 of 12</c> text came to print for eleven versions with no evaluator behind it (§8, B-2).
/// </remarks>
public enum PreRegisteredRuleVerdict
{
    /// <summary>
    /// The rule's own comparison ran and the challenger reached the required number of wins.
    /// </summary>
    Met,

    /// <summary>
    /// The rule's own comparison ran and the challenger did not reach the required number of wins.
    /// </summary>
    NotMet,

    /// <summary>
    /// The comparison the rule names could not be made. NOT a pass, NOT a fail, and never silent:
    /// the row prints with the reason attached.
    /// </summary>
    NotEvaluated,
}

/// <summary>
/// The verdict on one pre-registered decision rule, for one named pair of arms.
/// </summary>
/// <param name="Reference">The arm the rule measures against — the design's "single agent".</param>
/// <param name="Challenger">The arm the rule is about — the design's "workflow".</param>
/// <param name="Verdict">MET, NOT MET, or NOT EVALUATED.</param>
/// <param name="Wins">Wins for the challenger, or 0 when nothing was compared.</param>
/// <param name="Losses">Losses for the challenger, or 0 when nothing was compared.</param>
/// <param name="Ties">Ties, or 0 when nothing was compared.</param>
/// <param name="ComparableN">Pairs that were actually comparable — ties included.</param>
/// <param name="WinsRequired">The pre-registered threshold, carried so the row can print it.</param>
/// <param name="PreRegisteredPairs">The n the rule was pre-registered at.</param>
/// <param name="Reason">
/// One sentence. On NOT EVALUATED it says why the comparison could not be made; on the other two it
/// says which panel the counts came off.
/// </param>
public sealed record PreRegisteredRuleOutcome(
    string Reference,
    string Challenger,
    PreRegisteredRuleVerdict Verdict,
    int Wins,
    int Losses,
    int Ties,
    int ComparableN,
    int WinsRequired,
    int PreRegisteredPairs,
    string Reason)
{
    /// <summary>The verdict as the printer renders it.</summary>
    public string Label => Verdict switch
    {
        PreRegisteredRuleVerdict.Met => "MET",
        PreRegisteredRuleVerdict.NotMet => "NOT MET",
        _ => "NOT EVALUATED",
    };
}

/// <summary>
/// The evaluator behind the design's <c>≥ 10 of 12</c> decision rule — the thing §8/B-2 found
/// missing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists at all.</b> Eval 02 printed the sentence <i>"the workflow wins iff it
/// beats the single agent on ≥ 10 of 12 paired personas"</i> above a sign-test panel that had once
/// rendered a green <c>12/0/0</c> for a completely different comparison. There was no
/// <c>WinsRequired</c> anywhere in the repository, no threshold comparison and no verdict: a
/// quotable number in the shape of a met rule, on a pair the rule was not about. A rule that cannot
/// fail is not pre-registered, and a rule with no third outcome cannot say "not run" — so both are
/// here.
/// </para>
/// <para>
/// <b>Why NOT EVALUATED is the honest answer today.</b> The rule names the discovery WORKFLOW
/// against the single agent. Demo 2's arm runs on its deterministic path — zero model calls — so
/// pairing it against a model-backed live agent would move architecture and model presence in the
/// same comparison and neither operand could be read alone. That is why
/// <see cref="CoverageArm.EntersSignTest"/> is false for it, and it is exactly the state this
/// verdict has to be able to express. Since the k = 5 re-cut, a pair whose two sides presented
/// different numbers of items is NOT COMPARABLE, so a comparison can also collapse to zero
/// comparable pairs after the fact — also NOT EVALUATED, never a quiet pass.
/// </para>
/// <para>
/// <b>Superseded, and still evaluated.</b> Eval 09 carries the live four-clause rule for the
/// model-backed pairing. This one is kept and rendered rather than deleted because the design
/// pre-registered it, and a pre-registration that is silently dropped when it becomes inconvenient
/// is worth nothing. It prints its own supersession alongside its verdict.
/// </para>
/// </remarks>
public static class PreRegisteredRule
{
    /// <summary>
    /// The pre-registered threshold: the workflow wins iff it beats the single agent on at least
    /// this many paired personas (exact two-sided sign test, p = 0.0386 at n = 12).
    /// </summary>
    public const int WinsRequired = 10;

    /// <summary>The n the rule was pre-registered at.</summary>
    public const int PreRegisteredPairs = 12;

    /// <summary>The design's one-line statement of the rule, printed wherever the verdict is.</summary>
    public const string Statement =
        "the workflow wins iff it beats the single agent on ≥ 10 of 12 paired personas "
      + "(exact two-sided sign test, p = 0.0386)";

    /// <summary>Why this rule no longer decides anything on its own.</summary>
    public const string Supersession =
        "SUPERSEDED by Eval 09's four ordered clauses, which pair two MODEL-BACKED arms. This row is "
      + "kept and evaluated rather than deleted: a pre-registration dropped when it becomes "
      + "inconvenient is not a pre-registration.";

    /// <summary>
    /// Evaluates the rule for one reference/challenger pair against the sign tests a run produced.
    /// </summary>
    /// <remarks>
    /// The outcome is looked up BY ARM LABEL, never by position in the list — the positional index
    /// is the defect that once re-pointed Eval 02's second gate at a different comparison when an
    /// arm was inserted ahead of it.
    /// </remarks>
    /// <param name="referenceLabel">The reference arm's label (the single agent).</param>
    /// <param name="challengerLabel">The challenger arm's label (the workflow).</param>
    /// <param name="outcomes">Every sign-test outcome the run computed on the panel being read.</param>
    /// <param name="panel">Which panel those outcomes came off, for the printed reason.</param>
    public static PreRegisteredRuleOutcome Evaluate(
        string referenceLabel,
        string challengerLabel,
        IReadOnlyList<SignTestOutcome> outcomes,
        string panel)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        CoverageArm? challenger = CoverageArms.Find(challengerLabel);

        // ── The arm is not in the registry at all. ────────────────────────────────────────
        if (challenger is null)
        {
            return NotEvaluated(referenceLabel, challengerLabel,
                $"no arm named '{challengerLabel}' is registered, so the rule names a comparison this "
              + "suite cannot even describe.");
        }

        // ── The arm is DECLARED ABSENT. ──────────────────────────────────────────────────
        if (!challenger.IsRunnable)
        {
            return NotEvaluated(referenceLabel, challengerLabel,
                $"no second comparable entrant: '{challenger.Label}' is DECLARED ABSENT. {challenger.AbsenceReason}");
        }

        // ── The arm runs, but is deliberately not an entrant. ────────────────────────────
        if (!challenger.EntersSignTest)
        {
            return NotEvaluated(referenceLabel, challengerLabel,
                $"no second comparable entrant: '{challenger.Label}' RAN, but does not enter the sign test "
              + $"({challenger.Kind}). Pairing it against the model-backed live arm would move architecture "
              + "and model presence together, and neither operand could be read alone.");
        }

        var match = outcomes.FirstOrDefault(o =>
            string.Equals(o.ArmA, referenceLabel, StringComparison.Ordinal)
            && string.Equals(o.ArmB, challenger.Label, StringComparison.Ordinal));

        // ── It enters, but the run produced no outcome for the pair. ─────────────────────
        if (match is null)
        {
            return NotEvaluated(referenceLabel, challenger.Label,
                $"'{challenger.Label}' enters the sign test but no outcome for this pair appears on {panel}. "
              + "A rule whose comparison went missing is not a rule that passed.");
        }

        // ── It entered and every pair was refused. ───────────────────────────────────────
        if (match.Undecidable)
        {
            return new PreRegisteredRuleOutcome(
                referenceLabel, challenger.Label, PreRegisteredRuleVerdict.NotEvaluated,
                match.Wins, match.Losses, match.Ties, match.ComparedN, WinsRequired, PreRegisteredPairs,
                $"UNDECIDABLE on {panel}: {match.Excluded.Count} persona(s) were refused as NOT COMPARABLE and "
              + "zero pairs remained. Failing to compare is not the workflow winning.");
        }

        bool met = match.Wins >= WinsRequired;
        return new PreRegisteredRuleOutcome(
            referenceLabel, challenger.Label,
            met ? PreRegisteredRuleVerdict.Met : PreRegisteredRuleVerdict.NotMet,
            match.Wins, match.Losses, match.Ties, match.ComparedN, WinsRequired, PreRegisteredPairs,
            $"read off {panel}: the challenger won {match.Wins} of {match.ComparedN} comparable pair(s); "
          + $"the rule requires {WinsRequired} of {PreRegisteredPairs}."
          + (match.ComparedN < WinsRequired
                ? $" ⚠️ only {match.ComparedN} pair(s) were comparable, so no split of this run could have "
                + $"reached {WinsRequired} — the NOT MET above is a statement about the n, not about the arm."
                : match.ComparedN < PreRegisteredPairs
                    ? $" ⚠️ the rule was pre-registered at {PreRegisteredPairs} pairs and only {match.ComparedN} "
                    + "were comparable here; the threshold is NOT rescaled to the smaller n, because rescaling a "
                    + "pre-registered bar to the data is how a rule stops being pre-registered."
                    : ""));
    }

    private static PreRegisteredRuleOutcome NotEvaluated(string reference, string challenger, string reason) =>
        new(reference, challenger, PreRegisteredRuleVerdict.NotEvaluated,
            0, 0, 0, 0, WinsRequired, PreRegisteredPairs, reason);
}
