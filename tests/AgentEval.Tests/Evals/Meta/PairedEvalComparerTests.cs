// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 §4.5 and Slice 2.1. <b>The unit of analysis is the case, not the rep.</b>
/// </summary>
public class PairedEvalComparerTests
{
    private static PairedEvalComparer Seeded(RepCollapse collapse, params (string CaseId, string Arm, double Value)[] rows)
    {
        var comparer = new PairedEvalComparer(collapse);
        foreach (var (caseId, arm, value) in rows) comparer.Record(Observation.Measured(caseId, arm, value));
        return comparer;
    }

    // ── Reps collapse BEFORE pairing ──────────────────────────────────────────────────────────

    [Fact]
    public void RepsCollapseBeforePairing_SoPseudoReplicationIsImpossible()
    {
        // Three reps of two cases is TWO observations per arm, never six. Counting reps as
        // independent adds no information — they share a case, a prompt and a corpus row — but it
        // shrinks every standard error by sqrt(3) and every p-value with it.
        var comparer = Seeded(RepCollapse.Mean,
            ("c1", "ref", 0.0), ("c1", "ref", 0.0), ("c1", "ref", 0.0),
            ("c1", "new", 1.0), ("c1", "new", 1.0), ("c1", "new", 1.0),
            ("c2", "ref", 0.0), ("c2", "ref", 0.0), ("c2", "ref", 0.0),
            ("c2", "new", 1.0), ("c2", "new", 1.0), ("c2", "new", 1.0));

        var result = comparer.Compare("ref", "new");

        Assert.Equal(2, result.Wins);
        Assert.Equal(0, result.Losses);
        Assert.Equal(2, result.EffectiveN);
        Assert.Equal(2, result.Unit.Cases);
        Assert.Equal(12, result.Unit.TotalReps);
        Assert.Equal(3.0, result.Unit.MeanRepsPerCase, 12);

        // The number a reader needs on the page: had those 12 reps been paired as independent
        // observations, every standard error would have been understated by this factor.
        Assert.Equal(Math.Sqrt(3.0), result.Unit.PseudoReplicationInflation, 12);

        // n = 2 cannot reach 0.05 whatever happens, and the comparison says so instead of quoting
        // a p as though it could have been significant.
        Assert.True(result.UnderpoweredByConstruction);
    }

    [Fact]
    public void CollapseStrategies_EncodeDifferentClaims()
    {
        // Two reps: one pass, one fail. Five strategies, five different answers — which is exactly
        // why the strategy is DECLARED at construction rather than defaulted silently.
        double[] reps = [1.0, 0.0];

        Assert.Equal(0.5, ObservationUnit.Collapse(reps, RepCollapse.Mean), 12);
        Assert.Equal(0.5, ObservationUnit.Collapse(reps, RepCollapse.Median), 12);
        Assert.Equal(0.0, ObservationUnit.Collapse(reps, RepCollapse.All));
        Assert.Equal(0.0, ObservationUnit.Collapse(reps, RepCollapse.Majority));   // a split is a LOSS
        Assert.Equal(1.0, ObservationUnit.Collapse(reps, RepCollapse.Any));        // best-of-N

        // "Any" rises with N for free: the same arm, run more times, scores better. That is a
        // different claim, and IsBestOfN is what a renderer must label.
        Assert.Equal(0.0, ObservationUnit.Collapse([0.0], RepCollapse.Any));
        Assert.Equal(1.0, ObservationUnit.Collapse([0.0, 0.0, 1.0], RepCollapse.Any));
        Assert.True(new ObservationUnit(1, 3, 3.0, RepCollapse.Any).IsBestOfN);
        Assert.False(new ObservationUnit(1, 3, 3.0, RepCollapse.Mean).IsBestOfN);
    }

    [Fact]
    public void Collapse_OfNoReps_IsRefused_NotZero()
    {
        // An empty rep set is a case that did not run. Returning 0.0 would score it as a failure.
        Assert.Throws<ArgumentException>(() => ObservationUnit.Collapse([], RepCollapse.Mean));
    }

    [Fact]
    public void CollapsedCell_TakesTheWorstStateItsRepsCarry()
    {
        var comparer = new PairedEvalComparer(RepCollapse.Mean);
        comparer.Record(Observation.Measured("c1", "live", 1.0));
        comparer.Record(Observation.NotMeasured("c1", "live"));

        // One rep timed out, so the case was not measured. Averaging over the reps that survived
        // would silently change the denominator and report a number for a case nobody finished.
        Assert.Equal(MeasurementState.NotMeasured, comparer.CollapseCell("c1", "live")!.Value.State);

        var applicability = new PairedEvalComparer(RepCollapse.Mean);
        applicability.Record(Observation.Measured("c2", "live", 1.0));
        applicability.Record(Observation.NotApplicable("c2", "live"));
        Assert.Equal(MeasurementState.NotApplicable, applicability.CollapseCell("c2", "live")!.Value.State);

        Assert.Null(comparer.CollapseCell("nothing-recorded", "live"));
    }

    // ── Ties, exclusions, and the difference between them ─────────────────────────────────────

    [Fact]
    public void EveryPairTied_IsNotAWin()
    {
        var comparer = Seeded(RepCollapse.Mean,
            ("c1", "ref", 0.5), ("c1", "new", 0.5),
            ("c2", "ref", 0.5), ("c2", "new", 0.5));

        var result = comparer.Compare("ref", "new");

        Assert.Equal(0, result.EffectiveN);
        Assert.Equal(2, result.Ties);
        Assert.Equal(1.0, result.PValue);
        Assert.True(result.Undecidable);
        Assert.False(result.ChallengerLeads);
        Assert.Contains("UNDECIDABLE", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void NotApplicableOnEitherSide_ExcludesThePair_NeverTiesIt()
    {
        var comparer = new PairedEvalComparer(RepCollapse.Mean);
        comparer.Record(Observation.Measured("c1", "ref", 0.0));
        comparer.Record(Observation.NotApplicable("c1", "new"));
        comparer.Record(Observation.NotMeasured("c2", "ref"));
        comparer.Record(Observation.Measured("c2", "new", 1.0));
        comparer.Record(Observation.Measured("c3", "ref", 0.0));
        comparer.Record(Observation.Measured("c3", "new", 1.0));

        var result = comparer.Compare("ref", "new");

        // ⚠ The excluded pairs are counted in their own columns. Scoring an undecidable as a tie is
        // what makes "no difference found" out of "we could not look".
        Assert.Equal(1, result.Wins);
        Assert.Equal(0, result.Losses);
        Assert.Equal(0, result.Ties);
        Assert.Equal(new ObservationCensus(1, 1, 1), result.Census);
        Assert.Contains("1 n/a", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("1 not measured", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACaseOnlyOneArmProducedIsNotAPair()
    {
        var comparer = Seeded(RepCollapse.Mean,
            ("c1", "ref", 0.0), ("c1", "new", 1.0),
            ("c2", "new", 1.0));

        var result = comparer.Compare("ref", "new");

        Assert.Equal(1, result.Unit.Cases);
        Assert.Equal(1, result.EffectiveN);
    }

    // ── The exact test, and the power beside it ───────────────────────────────────────────────

    [Fact]
    public void Compare_RunsTheExactSignTestOverTheNonTiedPairs()
    {
        var comparer = new PairedEvalComparer(RepCollapse.Mean);
        for (int i = 0; i < 12; i++)
        {
            comparer.Record(Observation.Measured($"c{i}", "ref", 0.0));
            comparer.Record(Observation.Measured($"c{i}", "new", 1.0));
        }

        var result = comparer.Compare("ref", "new", ruleHash: "sha256:deadbeef");

        Assert.Equal(12, result.Wins);
        Assert.Equal(0.00048828125, result.PValue, 15);
        Assert.Equal(1.0, result.MeanDelta, 12);
        Assert.False(result.UnderpoweredByConstruction);
        Assert.True(result.ChallengerLeads);

        // The rule in force is stamped into the artefact, which is the only place a rule change is
        // actually detectable — between runs, in a diff.
        Assert.Equal("sha256:deadbeef", result.RuleHash);
    }

    [Fact]
    public void UnderpoweredByConstruction_IsAPropertyOfTheDesign()
    {
        // The recorded design: 13 cases, 9 tied, 4 informative, all four won by the challenger. The
        // minimum attainable two-sided p is 0.125, so a 4-0 sweep still cannot reach 0.05.
        var comparer = new PairedEvalComparer(RepCollapse.Mean);
        for (int i = 0; i < 9; i++)
        {
            comparer.Record(Observation.Measured($"tie{i}", "ref", 0.5));
            comparer.Record(Observation.Measured($"tie{i}", "new", 0.5));
        }

        for (int i = 0; i < 4; i++)
        {
            comparer.Record(Observation.Measured($"win{i}", "ref", 0.0));
            comparer.Record(Observation.Measured($"win{i}", "new", 1.0));
        }

        var result = comparer.Compare("ref", "new");

        Assert.Equal(4, result.Wins);
        Assert.Equal(9, result.Ties);
        Assert.Equal(0.125, result.MinimumAttainableP, 15);
        Assert.Equal(0.125, result.PValue, 15);
        Assert.True(result.UnderpoweredByConstruction);
        Assert.Contains("UNDERPOWERED BY CONSTRUCTION", result.Describe(), StringComparison.Ordinal);
    }

    // ── Declared absence renders; a missing arm does not ──────────────────────────────────────

    [Fact]
    public void DeclareAbsent_RendersWhereAMissingArmWouldNot()
    {
        var comparer = Seeded(RepCollapse.Mean, ("c1", "ref", 0.0), ("c1", "new", 1.0));
        comparer.DeclareAbsent("oracle", "the gold map was not available offline");

        var result = comparer.Compare("ref", "new");

        Assert.Contains("oracle", result.Absent.Keys, StringComparer.Ordinal);
        Assert.Contains("oracle", comparer.ArmIds(), StringComparer.Ordinal);
        Assert.DoesNotContain("never-mentioned", comparer.ArmIds(), StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() => comparer.DeclareAbsent("x", "  "));
    }

    [Fact]
    public void Compare_IsSnapshotOfAbsences_NotALiveView()
    {
        var comparer = Seeded(RepCollapse.Mean, ("c1", "ref", 0.0), ("c1", "new", 1.0));
        comparer.DeclareAbsent("oracle", "unavailable");

        var result = comparer.Compare("ref", "new");
        comparer.DeclareAbsent("second", "declared after the comparison was taken");

        // A result that mutates after it is produced is not a record of anything.
        Assert.Single(result.Absent);
    }
}
