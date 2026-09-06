// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 §4.3 and Slice 2.1/2.5. A chance floor is what an arm that understands nothing scores.
/// </summary>
/// <remarks>
/// The load-bearing rule under test is <b>an absent floor is not a zero floor</b>. A zero floor
/// makes every arm look above chance; that is how a metric gets condemned at p = 0.70.
/// </remarks>
public class ChanceFloorTests
{
    // ── The factories, against independently computed hypergeometric values ────────────────────

    [Fact]
    public void AtLeastOneHit_MatchesTheClosedForm()
    {
        // 1 - C(N-f, k)/C(N, k), computed outside this codebase.
        Assert.Equal(0.41966138960557997, ChanceFloor.AtLeastOneHit(99, 10, 5).Value, 12);
        Assert.Equal(0.1543547616478892, ChanceFloor.AtLeastOneHit(93, 3, 5).Value, 12);

        // The floor RISES with k — that is the whole reason k must come from a DECLARED budget.
        var atOne = ChanceFloor.AtLeastOneHit(99, 10, 1);
        var atEight = ChanceFloor.AtLeastOneHit(99, 10, 8);
        Assert.True(atEight.Value > atOne.Value);

        // Nothing favourable in the pool is a floor of 0 — reachable only by luck that does not exist.
        Assert.Equal(0.0, ChanceFloor.AtLeastOneHit(99, 0, 5).Value);

        // Every pool member favourable is a floor of 1: the metric cannot separate anything.
        Assert.Equal(1.0, ChanceFloor.AtLeastOneHit(99, 99, 5).Value, 12);
    }

    [Fact]
    public void AvoidsAll_IsHighByConstruction_AndSaysSo()
    {
        // The avoidance floor is the complement of the hit floor at the same k, which is exactly why
        // an avoidance rate printed without its floor reads as a safety result.
        Assert.Equal(0.58033861039442, ChanceFloor.AvoidsAll(99, 10, 5).Value, 12);
        Assert.Equal(
            1.0,
            ChanceFloor.AvoidsAll(99, 10, 5).Value + ChanceFloor.AtLeastOneHit(99, 10, 5).Value,
            12);

        // An arm that presents FEWER items scores better against it. Presenting nothing is a
        // certainty, and the floor is 1.0 — the number that stops silence reading as safety.
        Assert.True(ChanceFloor.AvoidsAll(99, 10, 1).Value > ChanceFloor.AvoidsAll(99, 10, 8).Value);
    }

    [Fact]
    public void UniformChoice_RefusesASingleAlternative()
    {
        Assert.Equal(1.0 / 12.0, ChanceFloor.UniformChoice(12).Value, 15);

        // ⚠ One alternative is NOT a floor of 1.0 — it is a question with one answer, and scoring an
        // arm against it says nothing about the arm. Not derivable, and the reason prints.
        var degenerate = ChanceFloor.UniformChoice(1);
        Assert.Equal(FloorState.NotDerivable, degenerate.State);
        Assert.Throws<InvalidOperationException>(() => degenerate.Value);
        Assert.Contains("one answer", degenerate.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorRate_IsTheBaseRate_AndRefusesAnEmptyCorpus()
    {
        Assert.Equal(0.25, ChanceFloor.PriorRate(3, 12).Value, 15);
        Assert.Equal(FloorState.NotDerivable, ChanceFloor.PriorRate(0, 0).State);
    }

    // ── An absent floor is not a zero floor ───────────────────────────────────────────────────

    [Fact]
    public void NotDerivableFloor_ThrowsRatherThanReturningZero()
    {
        var floor = ChanceFloor.NotDerivable("the gold set is empty, so nothing could have been served by luck");

        Assert.Equal(FloorState.NotDerivable, floor.State);
        Assert.True(double.IsNaN(floor.RawValue));

        // Value and ComparisonBar both refuse: a caller cannot average an absence into a mean, and
        // cannot slip one into a comparison through the bar either.
        var thrown = Assert.Throws<InvalidOperationException>(() => floor.Value);
        Assert.Contains("not a zero floor", thrown.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => floor.ComparisonBar);
    }

    [Fact]
    public void NotDerivable_RequiresAReason()
    {
        Assert.Throws<ArgumentException>(() => ChanceFloor.NotDerivable("   "));
        Assert.Throws<ArgumentNullException>(() => ChanceFloor.NotDerivable(null!));
    }

    [Fact]
    public void EveryFactoryProducesANonEmptyDerivation()
    {
        // "One sentence naming the pool, the favourable set and k. Never empty." A floor with no
        // derivation is a constant somebody typed.
        ChanceFloor[] floors =
        [
            ChanceFloor.AtLeastOneHit(99, 10, 5),
            ChanceFloor.AvoidsAll(99, 10, 5),
            ChanceFloor.UniformChoice(12),
            ChanceFloor.PriorRate(3, 12),
            ChanceFloor.Empirical(4, 12, policiesConsidered: 1),
            ChanceFloor.NotDerivable("nothing to derive from"),
        ];

        Assert.All(floors, f => Assert.False(string.IsNullOrWhiteSpace(f.Derivation)));
    }

    // ── 2.5 · an empirical floor is a MAXIMUM over a family unless it says otherwise ───────────

    [Fact]
    public void SelectedFloor_RequiresHeldOutSplit()
    {
        // "The best constant policy" is a maximum over a family, and a maximum selected on the same
        // corpus the agent is scored on is optimistically biased. The recorded instance: a ceiling
        // TYPED as 8 and MEASURED at 10.
        var thrown = Assert.Throws<ArgumentException>(
            () => ChanceFloor.Empirical(10, 14, policiesConsidered: 4));

        Assert.Equal("heldOutFrom", thrown.ParamName);
        Assert.Contains("MAXIMUM over a family", thrown.Message, StringComparison.Ordinal);

        // Naming the split is what makes it legal, and the split travels into the derivation.
        var honest = ChanceFloor.Empirical(10, 14, policiesConsidered: 4, heldOutFrom: "the ten fitted customers");
        Assert.Equal(FloorState.Derived, honest.State);
        Assert.Contains("the ten fitted customers", honest.Derivation, StringComparison.Ordinal);

        // One policy considered is not a selection, so it needs no split.
        Assert.Equal(FloorState.Derived, ChanceFloor.Empirical(10, 14, policiesConsidered: 1).State);

        // Zero policies is a caller bug, not a permissive default.
        Assert.Throws<ArgumentOutOfRangeException>(() => ChanceFloor.Empirical(10, 14, policiesConsidered: 0));
    }

    [Fact]
    public void EmpiricalFloor_IsComparedAgainstItsIntervalBound_NotItsPoint()
    {
        // Comparing an observed rate to a point estimate computed from the same corpus is the
        // co-moving-operands failure. An estimated floor therefore carries its own uncertainty and
        // ComparisonBar returns THAT.
        var estimated = ChanceFloor.Empirical(4, 12, policiesConsidered: 1);

        Assert.True(estimated.WasEstimated);
        Assert.NotNull(estimated.IntervalHigh);
        Assert.True(estimated.ComparisonBar > estimated.Value,
            "an estimated floor's bar must be its upper bound, or the comparison is against a number that moved with the data");

        // An ANALYTIC floor is exact, so its bar is its value and nothing is inflated for show.
        var analytic = ChanceFloor.UniformChoice(12);
        Assert.False(analytic.WasEstimated);
        Assert.Equal(analytic.Value, analytic.ComparisonBar);
    }

    // ── FloorComparison ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompareToFloor_ExcludesUndecidables_FromTheDenominator()
    {
        var floor = ChanceFloor.UniformChoice(12);
        Observation[] obs =
        [
            Observation.Measured("c1", "live", 1.0),
            Observation.Measured("c2", "live", 1.0),
            Observation.Measured("c3", "live", 1.0),
            Observation.Measured("c4", "live", 1.0),
            Observation.NotApplicable("c5", "live"),
            Observation.NotMeasured("c6", "live"),
            Observation.Measured("c1", "control", 0.0),
        ];

        var comparison = FloorComparison.Compute(obs, "live", floor);

        // The four measured cases are the denominator. A diluted denominator (6, or 7 with the
        // other arm's row) is the shape that makes a gate report "12 of 12" while reading three.
        Assert.Equal(4, comparison.Trials);
        Assert.Equal(4, comparison.Successes);
        Assert.Equal(new ObservationCensus(4, 1, 1), comparison.Census);

        // 4 of 4 against 1/12 is p = (1/12)^4, comfortably significant — and the design could have
        // reached alpha, so the direction is readable.
        Assert.True(comparison.AboveFloor);
        Assert.False(comparison.UnderpoweredByConstruction);
    }

    [Fact]
    public void CompareToFloor_UnderpoweredDesign_CannotReportAbove()
    {
        // One trial against a 1/2 floor: the most extreme observation possible is p = 0.5. No
        // observation at this n could have reached alpha, and AboveFloor must say so rather than
        // reporting a direction the design could not support.
        var floor = ChanceFloor.UniformChoice(2);
        Observation[] obs = [Observation.Measured("c1", "live", 1.0)];

        var comparison = FloorComparison.Compute(obs, "live", floor);

        Assert.Equal(1, comparison.Trials);
        Assert.True(comparison.UnderpoweredByConstruction);
        Assert.False(comparison.AboveFloor);
    }

    [Fact]
    public void CompareToFloor_RefusesAFractionalValue()
    {
        // The measured defect this refusal exists for: a per-case rep-mean of 0.778 tested as
        // "2 of 3" reads p = 0.063 (not above) where the correct null reads p = 0.002 (well above).
        // Rounding a mean into a success count is a per-case verdict flip, not a rounding nicety.
        var floor = ChanceFloor.UniformChoice(12);
        Observation[] obs =
        [
            Observation.Measured("c1", "live", 1.0),
            Observation.Measured("c2", "live", 0.7777777777777778),
        ];

        var thrown = Assert.Throws<ArgumentException>(() => FloorComparison.Compute(obs, "live", floor));

        Assert.Contains("c2", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Bernoulli", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareToFloor_AgainstANotDerivableFloor_IsNotAPass()
    {
        var floor = ChanceFloor.NotDerivable("no eligible pool for this case");
        Observation[] obs =
        [
            Observation.Measured("c1", "live", 1.0),
            Observation.Measured("c2", "live", 1.0),
            Observation.Measured("c3", "live", 1.0),
            Observation.Measured("c4", "live", 1.0),
            Observation.Measured("c5", "live", 1.0),
        ];

        var comparison = FloorComparison.Compute(obs, "live", floor);

        // A perfect arm against an absent floor is still undecidable. NaN, not 0.0, and never above.
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);
        Assert.Equal(5, comparison.Trials);
    }

    [Fact]
    public void CompareToFloor_OnNoObservations_IsVoid_NotPerfect()
    {
        var comparison = FloorComparison.Compute([], "live", ChanceFloor.UniformChoice(12));

        Assert.Equal(0, comparison.Trials);
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);
        Assert.Equal(1.0, comparison.MinimumAttainableP);
    }

    // ── ArmProfile ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArmProfile_CarriesTheProvenanceOfK()
    {
        // A k with no provenance is a k someone tuned. The record cannot enforce truthfulness, but
        // it can make the field impossible to omit, which is what a review checklist reads.
        var profile = new ArmProfile("live", 5, "the utterance's declared budget");

        Assert.Equal(5, profile.DeclaredDrawBudget);
        Assert.False(string.IsNullOrWhiteSpace(profile.BudgetSource));
    }
}
