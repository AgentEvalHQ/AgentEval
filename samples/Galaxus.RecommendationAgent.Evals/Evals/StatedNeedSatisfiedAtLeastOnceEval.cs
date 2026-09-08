// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Evals.Cases;
using Galaxus.RecommendationAgent.Evals.Graders;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 02b's deterministic verdict: did at least one presented product satisfy the customer's
/// stated need?
/// </summary>
/// <remarks>
/// <para>
/// The strongest claim this suite makes, reduced to something a code check can decide: the customer
/// stated a budget, a constraint, a deadline; the satisfying set is computable from the catalogue;
/// either something from it reached the customer or nothing did.
/// </para>
/// <para>
/// 🔴 <b>Applicability is read from the INPUT, before the output is looked at.</b> A case no
/// catalogue product can satisfy is undecidable no matter what the arm does, and deciding that from
/// the arm's output would be the repeated defect of letting the artifact under test choose whether
/// it is graded.
/// </para>
/// <para>
/// ⚠ <b>The floor varies per case</b> — favourable is that case's satisfying count. That is
/// admissible at the door, because the floor is recorded on each row and <c>compare</c> reads it per
/// scenario. It is NOT poolable into one binomial tail by <c>FloorComparison.Compute</c>, which
/// takes one floor per arm (ADR-032 D13). Recorded rather than worked around.
/// </para>
/// </remarks>
/// <param name="testCase">The stated-need case under test.</param>
public sealed class StatedNeedSatisfiedAtLeastOnceEval(StatedNeedCase testCase)
    : AtomicCodeEval(EvalKey, "Stated need satisfied at least once", "recommendation.constraint", "1.0.0")
{
    /// <summary>This eval's key, frozen in one place because the admission door refuses duplicates.</summary>
    public const string EvalKey = "stated_need_satisfied_at_least_once";

    private readonly StatedNeedCase _case = testCase ?? throw new ArgumentNullException(nameof(testCase));

    /// <summary>
    /// The floor for one case: how often a uniform draw of
    /// <see cref="ChanceFloors.DegenerateDrawSize"/> products contains at least one member of that
    /// case's satisfying set.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>An empty satisfying set does NOT get a floor of zero.</b>
    /// <c>ChanceFloor.AtLeastOneHit(N, 0, k)</c> does not throw and does not return
    /// <see cref="FloorState.NotDerivable"/> — it clamps <c>favourable</c> to 0, computes
    /// <c>miss = 1.0</c>, and returns a <b>Derived</b> floor of <b>0.0</b>
    /// (<c>ChanceFloor.cs:118-129</c>). A zero recorded as a bar is a bar everything clears, which is
    /// precisely the "an absent floor is not a zero floor" defect. So the empty case is declared
    /// <see cref="ChanceFloor.NotDerivable"/> explicitly.
    /// </remarks>
    /// <param name="testCase">The case whose floor is wanted.</param>
    public static ChanceFloor FloorFor(StatedNeedCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var satisfying = ConstraintSatisfactionGrader.SatisfyingSet(testCase).Count;

        return satisfying > 0
            ? ChanceFloor.AtLeastOneHit(
                poolSize: Catalogue.Default.All.Count,
                favourable: satisfying,
                draws: ChanceFloors.DegenerateDrawSize)
            : ChanceFloor.NotDerivable(
                "no catalogue product satisfies this case, so there is no draw model: a floor of 0.0 "
                + "would be a bar everything clears, and this case has no bar at all.");
    }

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var satisfying = ConstraintSatisfactionGrader.SatisfyingSet(_case);

        // INPUT-side applicability: decided before the output is read.
        if (satisfying.Count == 0)
        {
            var reason =
                "no product in the catalogue satisfies this stated need, so no answer could have "
                + "satisfied it. The case is undecidable on its own terms, whatever the arm returned.";
            return NotApplicable(reason, new EvalEvidence("catalogue", _case.Id, reason));
        }

        if (input.ToolCalls is null)
        {
            var blind = PresentedSkuReader.BlindRecorderReason("whether the stated need was satisfied");
            return NotApplicable(blind, new EvalEvidence("tool-calls", PresentedCall.ToolName, blind));
        }

        var presented = PresentedSkuReader.From(input.ToolCalls);
        var satisfyingSkus = satisfying.Select(p => p.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = presented.Where(satisfyingSkus.Contains).ToList();
        var satisfied = hits.Count > 0;

        var summary = satisfied
            ? $"{hits.Count} of the {presented.Count} product(s) presented satisfy the stated need "
              + $"({string.Join(", ", hits)}); the satisfying set has {satisfying.Count} member(s)."
            : $"none of the {presented.Count} product(s) presented is among the {satisfying.Count} "
              + "product(s) that satisfy this stated need.";

        var scored = Build(
            value: satisfied ? 1.0 : 0.0,
            passed: satisfied,
            severity: satisfied ? "none" : "high",
            dimensions: null,
            evidence: [new EvalEvidence("tool-calls", PresentedCall.ToolName, summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
