// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Scoring is facts about runs: successes, trials, a tail, a census. Never a verdict, and never
/// over the rep.
/// </summary>
public class BenchmarkScoreTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class StubEval(string key) : IEval
    {
        public string Key { get; } = key;
        public string Name => "stub";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            throw new NotSupportedException("Scoring never runs an eval; it reads what a run recorded.");
    }

    private const string CheckKey = "k1";

    /// <summary>An easy floor: one uniform choice in four. Nothing here is near the ceiling.</summary>
    private static readonly ChanceFloor EasyFloor = ChanceFloor.UniformChoice(4);

    /// <summary>
    /// A floor AT the ceiling: avoid all of zero forbidden items in a pool of ten, and a uniform draw
    /// succeeds with certainty. A bar nothing can fail to clear.
    /// </summary>
    private static readonly ChanceFloor CeilingFloor = ChanceFloor.AvoidsAll(poolSize: 10, forbidden: 0, draws: 3);

    private static TestCase Case(string id) => new() { Id = id, Name = id, Input = "q" };

    private static EvalResult PlainResult(double value) => new(
        new(CheckKey, "stub", "test", "1.0.0"),
        new(value, null, value >= 1.0 ? "pass" : "fail", value >= 1.0, null, "none", null),
        new(null, null, null, null, null),
        new("atomic-code", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);

    private static BenchmarkDefinition Definition(ChanceFloor floor, params string[] caseIds) =>
        new("bench", "1.0.0",
            [.. caseIds.Select(Case)],
            [new AdmittedCheck(new StubEval(CheckKey), floor)]);

    /// <summary>One rep of one arm: a value per case, in the definition's case order.</summary>
    private static BenchmarkRun Run(
        BenchmarkDefinition definition, string runId, string armId, params double[] valuePerCase)
    {
        Assert.Equal(definition.Cases.Count, valuePerCase.Length);

        var observations = definition.Cases
            .Select((c, i) => new CheckObservation(
                CheckKey,
                Observation.Measured(c.Id!, armId, valuePerCase[i]),
                PlainResult(valuePerCase[i])))
            .ToList();

        return new BenchmarkRun(runId, armId, definition, observations);
    }

    private static BenchmarkRun RunWithStates(
        BenchmarkDefinition definition, string runId, string armId, params MeasurementState[] statePerCase)
    {
        Assert.Equal(definition.Cases.Count, statePerCase.Length);

        var observations = definition.Cases
            .Select((c, i) => new CheckObservation(
                CheckKey,
                statePerCase[i] switch
                {
                    MeasurementState.NotApplicable => Observation.NotApplicable(c.Id!, armId),
                    MeasurementState.NotMeasured => Observation.NotMeasured(c.Id!, armId),
                    _ => Observation.Measured(c.Id!, armId, 1.0),
                },
                PlainResult(0.0)))
            .ToList();

        return new BenchmarkRun(runId, armId, definition, observations);
    }

    // ── The ceiling ───────────────────────────────────────────────────────────

    [Fact]
    public void AFloorAtCeiling_IsUndecidable_NeverAPass()
    {
        var definition = Definition(CeilingFloor, "c1", "c2", "c3", "c4", "c5");
        var runs = new[] { Run(definition, "r1", "live", 1, 1, 1, 1, 1) };

        var scored = BenchmarkScore.AgainstFloor(runs);
        var comparison = scored.Single().Comparison;

        Assert.Equal(1.0, comparison.FloorUsed, 10);
        Assert.Equal(5, comparison.Successes);
        Assert.Equal(5, comparison.Trials);

        // A perfect arm against a bar luck clears with certainty. Not significant, and it never was.
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);
    }

    [Fact]
    public void TheSamePerfectArm_AgainstADerivableFloor_IsAbove()
    {
        // The other direction on the same assertion: the 5-of-5 above is not "the code always says
        // no". Swap only the floor and the identical observations clear it.
        var definition = Definition(EasyFloor, "c1", "c2", "c3", "c4", "c5");
        var runs = new[] { Run(definition, "r1", "live", 1, 1, 1, 1, 1) };

        var comparison = BenchmarkScore.AgainstFloor(runs).Single().Comparison;

        Assert.Equal(0.25, comparison.FloorUsed, 10);
        Assert.False(double.IsNaN(comparison.PValue));
        Assert.True(comparison.AboveFloor);
    }

    [Fact]
    public void ANotDerivableFloor_IsAlsoUndecidable_AndNotAZero()
    {
        // An absent floor is not a floor of 0.0 — a bar everything clears. It is a NaN tail.
        var definition = Definition(ChanceFloor.NotDerivable("no draw model exists for this property"), "c1", "c2");
        var runs = new[] { Run(definition, "r1", "live", 1, 1) };

        var comparison = BenchmarkScore.AgainstFloor(runs).Single().Comparison;

        Assert.True(double.IsNaN(comparison.FloorUsed));
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);
    }

    // ── Reps collapse per case, BEFORE the tail ───────────────────────────────

    [Fact]
    public void RepsAreCollapsedPerCase_BeforeTheTail()
    {
        // Four cases, three reps each. Case c1 passes on every rep; c2/c3/c4 pass on exactly one.
        // "It does this every time" counts 1 success; "best of 3" counts 4. Same 12 numbers.
        var definition = Definition(EasyFloor, "c1", "c2", "c3", "c4");
        var runs = new[]
        {
            Run(definition, "r1", "live", 1, 1, 0, 0),
            Run(definition, "r2", "live", 1, 0, 1, 0),
            Run(definition, "r3", "live", 1, 0, 0, 1),
        };

        var all = BenchmarkScore.AgainstFloor(runs, RepCollapse.All).Single().Comparison;
        var any = BenchmarkScore.AgainstFloor(runs, RepCollapse.Any).Single().Comparison;

        Assert.Equal(1, all.Successes);
        Assert.Equal(4, any.Successes);

        // And the point of collapsing at all: n is the CASE count under both strategies, never the
        // 12 rep-observations that went in. Pseudo-replication would report 12.
        Assert.Equal(4, all.Trials);
        Assert.Equal(4, any.Trials);
    }

    [Fact]
    public void ACellWithOneUnmeasuredRep_CollapsesToTheWorstState_NotToTheSurvivingMean()
    {
        // The rule an open-coded collapse over the VALUES would silently drop. c1 was measured on two
        // reps and not measured on the third: averaging the survivors would report a score where the
        // instrument did not run, and would keep c1 in the denominator.
        var definition = Definition(EasyFloor, "c1", "c2");
        var runs = new[]
        {
            Run(definition, "r1", "live", 1, 1),
            Run(definition, "r2", "live", 1, 1),
            RunWithStates(definition, "r3", "live", MeasurementState.NotMeasured, MeasurementState.Measured),
        };

        var comparison = BenchmarkScore.AgainstFloor(runs, RepCollapse.All).Single().Comparison;

        Assert.Equal(1, comparison.Trials);                 // c1 left the denominator
        Assert.Equal(1, comparison.Successes);              // c2 survived
        Assert.Equal(1, comparison.Census.NotMeasured);     // and c1 is counted, not lost
    }

    [Fact]
    public void AMeanCollapse_IsRefusedByTheTail_RatherThanRounded()
    {
        // RepCollapse.Mean is a legal argument and produces a fractional per-case value, which is not
        // a Bernoulli outcome. The tail REFUSES it instead of rounding: measured on this repository's
        // own corpus, a rep-mean of 0.778 tested as "2 of 3" reads p = 0.063 where the correct null
        // reads p = 0.002 — a per-case verdict flip caused entirely by the rounding.
        var definition = Definition(EasyFloor, "c1", "c2");
        var runs = new[]
        {
            Run(definition, "r1", "live", 1, 1),
            Run(definition, "r2", "live", 1, 1),
            Run(definition, "r3", "live", 0, 1),
        };

        var ex = Assert.Throws<ArgumentException>(() => BenchmarkScore.AgainstFloor(runs, RepCollapse.Mean));

        Assert.Contains("Bernoulli trials", ex.Message, StringComparison.Ordinal);
        Assert.Contains("c1", ex.Message, StringComparison.Ordinal);
    }

    // ── Census ────────────────────────────────────────────────────────────────

    [Fact]
    public void CensusIsVoid_WhenNothingWasMeasured()
    {
        var definition = Definition(EasyFloor, "c1", "c2", "c3");
        var runs = new[]
        {
            RunWithStates(definition, "r1", "live",
                MeasurementState.NotApplicable, MeasurementState.NotApplicable, MeasurementState.NotMeasured),
        };

        var census = BenchmarkScore.Census(runs).Single().Census;

        Assert.Equal(0, census.Measured);
        Assert.Equal(2, census.NotApplicable);
        Assert.Equal(1, census.NotMeasured);
        Assert.Equal(3, census.Total);
        Assert.True(census.Void);
    }

    [Fact]
    public void CensusCountsCases_NotReps()
    {
        // The denominator the number was computed over, not the number of observations recorded. A
        // census over raw reps would say 6 and make a 2-rep run look twice as well powered.
        var definition = Definition(EasyFloor, "c1", "c2", "c3");
        var runs = new[]
        {
            Run(definition, "r1", "live", 1, 1, 1),
            Run(definition, "r2", "live", 1, 1, 1),
        };

        var census = BenchmarkScore.Census(runs).Single().Census;

        Assert.Equal(3, census.Total);
        Assert.Equal(3, census.Measured);
        Assert.False(census.Void);
    }

    // ── Paired comparison ─────────────────────────────────────────────────────

    [Fact]
    public void AgainstReference_UsesTheCaseAsTheUnit()
    {
        // Five cases, three reps each = 30 recorded observations across both arms. The exact test
        // runs on 5.
        var definition = Definition(EasyFloor, "c1", "c2", "c3", "c4", "c5");
        var reference = new[]
        {
            Run(definition, "b1", "baseline", 0, 0, 0, 0, 0),
            Run(definition, "b2", "baseline", 0, 0, 0, 0, 0),
            Run(definition, "b3", "baseline", 0, 0, 0, 0, 0),
        };
        var challenger = new[]
        {
            Run(definition, "l1", "live", 1, 1, 1, 1, 1),
            Run(definition, "l2", "live", 1, 1, 1, 1, 1),
            Run(definition, "l3", "live", 1, 1, 1, 1, 1),
        };

        var comparison = BenchmarkScore.AgainstReference(reference, challenger).Single().Comparison;

        Assert.Equal(5, comparison.EffectiveN);
        Assert.Equal(5, comparison.Wins);
        Assert.Equal(0, comparison.Losses);
        Assert.Equal(5, comparison.Unit.Cases);
        Assert.Equal(30, comparison.Unit.TotalReps);
    }

    [Fact]
    public void AgainstReference_ExcludesAPairWhereEitherSideCouldNotBeMeasured()
    {
        // Never a tie, never a loss. An undecidable scored as a tie is how "we could not look"
        // becomes "no difference".
        var definition = Definition(EasyFloor, "c1", "c2");
        var reference = new[] { Run(definition, "b1", "baseline", 0, 0) };
        var challenger = new[]
        {
            RunWithStates(definition, "l1", "live", MeasurementState.Measured, MeasurementState.NotApplicable),
        };

        var comparison = BenchmarkScore.AgainstReference(reference, challenger).Single().Comparison;

        Assert.Equal(1, comparison.EffectiveN);
        Assert.Equal(1, comparison.Wins);
        Assert.Equal(0, comparison.Ties);
        Assert.Equal(1, comparison.Census.NotApplicable);
    }

    [Fact]
    public void AgainstReference_RefusesTwoSidesOfTheSameArm()
    {
        var definition = Definition(EasyFloor, "c1");
        var runs = new[] { Run(definition, "r1", "live", 1) };

        var ex = Assert.Throws<ArgumentException>(() => BenchmarkScore.AgainstReference(runs, runs));

        Assert.Contains("cannot lose is not a comparison", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgainstReference_RefusesTwoDifferentDefinitions()
    {
        var v1 = Definition(EasyFloor, "c1");
        var v2 = v1 with { Version = "2.0.0" };

        var ex = Assert.Throws<ArgumentException>(() => BenchmarkScore.AgainstReference(
            [Run(v1, "b1", "baseline", 0)], [Run(v2, "l1", "live", 1)]));

        Assert.Contains("bench@1.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bench@2.0.0", ex.Message, StringComparison.Ordinal);
    }

    // ── What an empty or mixed run set is, and is not ─────────────────────────

    [Fact]
    public void NoRuns_IsRefused_NotScoredAsZero()
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchmarkScore.AgainstFloor([]));

        Assert.Contains("not a zero score", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunsOfTwoDifferentArms_AreRefused()
    {
        var definition = Definition(EasyFloor, "c1");

        var ex = Assert.Throws<ArgumentException>(() => BenchmarkScore.AgainstFloor(
            [Run(definition, "r1", "live", 1), Run(definition, "r2", "baseline", 0)]));

        Assert.Contains("reps of ONE arm", ex.Message, StringComparison.Ordinal);
    }

    // ── The lane rule ─────────────────────────────────────────────────────────

    [Fact]
    public void BenchmarkScore_IsNotAnIEval_AndNoMemberReturnsAnEvalResult()
    {
        var type = typeof(BenchmarkScore);

        Assert.False(typeof(IEval).IsAssignableFrom(type));
        Assert.True(type.IsAbstract && type.IsSealed, "BenchmarkScore must be a static class: it holds no state.");

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Assert.False(Mentions(method.ReturnType, typeof(EvalResult)),
                $"{method.Name} returns something carrying an EvalResult. A meta type that can return a "
                + "result model gives AgentEval one more result model, and it is the one holding pass/fail "
                + "authority (ADR-030 §4.6).");
        }
    }

    private static bool Mentions(Type type, Type target)
    {
        if (type == target) return true;
        if (!type.IsGenericType) return false;

        foreach (var argument in type.GetGenericArguments())
        {
            if (Mentions(argument, target)) return true;
        }

        return false;
    }
}
