// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ADR-031 S5 — the comparison decision, as a pure function of two runs' scenario
// results. `agenteval compare`'s exit code is a rendering of RunComparison.Verdict,
// so this is where the rule is pinned.
//
// The first attempt at S5 was refuted for having ONE reachable outcome, so the
// tests below assert BOTH directions on every axis, and assert that the success
// path is reachable on the fact shape real producers actually write.

using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

public class RunComparisonTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static ScenarioResult Scenario(
        string id = "s1",
        double score = 0.5,
        bool passed = true,
        string? stimulusHash = "sha256:aaa",
        ComparabilityFacts? comparability = null) =>
        new(id, id, "in", "out", passed, score,
            new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0)
        {
            StimulusHash = stimulusHash,
            Comparability = comparability ?? Facts(),
        };

    private static ComparabilityFacts Facts(
        string key = "eval.k",
        string version = "1.0.0",
        double? bar = 0.7,
        JudgeFingerprint? judge = null,
        RecordedChanceFloor? floor = null) =>
        new(key, version)
        {
            EffectiveBar = bar,
            Judge = judge ?? new JudgeFingerprint("gpt-5.5"),
            ChanceFloor = floor,
        };

    private static RunComparison Compare(
        ScenarioResult a, ScenarioResult b, bool strict = false) =>
        RunComparison.Of([a], [b], strict);

    private static ComparabilityAxis Axis(RunComparison c, string name) =>
        c.Scenarios.Single().Axes.Single(x => x.Name == name);

    // ── The success path is reachable — the clause the first S5 was refuted on ──

    [Fact]
    public void TwoIdenticalRuns_AreComparable()
    {
        var c = Compare(Scenario(), Scenario());

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Empty(c.RefusalReasons);
        Assert.Single(c.Scenarios);
    }

    [Fact] // The shape real producers write today: no rubric digest, no chance floor.
    public void TheFactShapeRealProducersWrite_IsComparable_WithoutStrict()
    {
        var facts = Facts(judge: new JudgeFingerprint("gpt-5.5", RubricDigest: null), floor: null);
        var c = Compare(
            Scenario(comparability: facts),
            Scenario(comparability: facts));

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Unpinned, Axis(c, "judge.rubricDigest").State);
        Assert.Single(c.ScenariosWithoutAFloor);
    }

    // ── Every gating axis, in BOTH directions ────────────────────────────────

    [Fact]
    public void DifferentEvalKey_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(key: "eval.a")),
            Scenario(comparability: Facts(key: "eval.b")));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "evalKey").State);
    }

    [Fact]
    public void DifferentEvalVersion_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(version: "1.0.0")),
            Scenario(comparability: Facts(version: "1.1.0")));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "evalVersion").State);
    }

    [Fact]
    public void DifferentEffectiveBar_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(bar: 0.7)),
            Scenario(comparability: Facts(bar: 0.8)));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "effectiveBar").State);
    }

    [Fact] // "one eval declared a bar and the other declared none" is a difference, not a gap.
    public void BarOnOneSideOnly_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(bar: 0.7)),
            Scenario(comparability: Facts(bar: null)));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "effectiveBar").State);
    }

    [Fact] // …and neither side declaring one is UNPINNED, not a match and not a mismatch.
    public void BarOnNeitherSide_IsUnpinned_NotAMatch()
    {
        var c = Compare(
            Scenario(comparability: Facts(bar: null)),
            Scenario(comparability: Facts(bar: null)));

        Assert.Equal(ComparabilityAxisState.Unpinned, Axis(c, "effectiveBar").State);
        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Equal(ComparisonVerdict.Incomparable, Compare(
            Scenario(comparability: Facts(bar: null)),
            Scenario(comparability: Facts(bar: null)), strict: true).Verdict);
    }

    [Fact]
    public void DifferentJudgeModel_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(judge: new JudgeFingerprint("gpt-5.5"))),
            Scenario(comparability: Facts(judge: new JudgeFingerprint("gpt-4o"))));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "judge.modelId").State);
    }

    [Fact] // One judged, one deterministic.
    public void JudgeOnOneSideOnly_IsIncomparable()
    {
        var withJudge = Facts(judge: new JudgeFingerprint("gpt-5.5"));
        var withoutJudge = new ComparabilityFacts("eval.k", "1.0.0") { EffectiveBar = 0.7, Judge = null };

        var c = Compare(Scenario(comparability: withJudge), Scenario(comparability: withoutJudge));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "judge").State);
    }

    [Fact] // A null judge is a DECLARED fact — "this eval used none" — so both-null is a MATCH.
    public void NoJudgeOnEitherSide_IsAMatch_NotUnpinned()
    {
        var deterministic = new ComparabilityFacts("eval.k", "1.0.0") { EffectiveBar = 0.7, Judge = null };
        var c = Compare(Scenario(comparability: deterministic), Scenario(comparability: deterministic));

        Assert.Equal(ComparabilityAxisState.Match, Axis(c, "judge").State);
        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
    }

    [Fact]
    public void DifferentRubricDigest_IsIncomparable()
    {
        var c = Compare(
            Scenario(comparability: Facts(judge: new JudgeFingerprint("gpt-5.5", "sha256:aaa"))),
            Scenario(comparability: Facts(judge: new JudgeFingerprint("gpt-5.5", "sha256:bbb"))));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "judge.rubricDigest").State);
    }

    // ── The stimulus, and the null rule S2 exists for ────────────────────────

    [Fact]
    public void DifferentStimulus_IsIncomparable()
    {
        var c = Compare(Scenario(stimulusHash: "sha256:aaa"), Scenario(stimulusHash: "sha256:bbb"));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "stimulus").State);
    }

    [Fact] // "Nobody computed a digest" is not "the digests match" — the whole point of S2's rule.
    public void StimulusOnNeitherSide_IsUnpinned_AndStrictRefuses()
    {
        var lax = Compare(Scenario(stimulusHash: null), Scenario(stimulusHash: null));
        var strict = Compare(Scenario(stimulusHash: null), Scenario(stimulusHash: null), strict: true);

        Assert.Equal(ComparabilityAxisState.Unpinned, Axis(lax, "stimulus").State);
        Assert.NotEqual(ComparabilityAxisState.Match, Axis(lax, "stimulus").State);
        Assert.Equal(ComparisonVerdict.Comparable, lax.Verdict);
        Assert.Equal(ComparisonVerdict.Incomparable, strict.Verdict);
    }

    [Fact]
    public void StimulusOnOneSideOnly_IsIncomparable_EvenWithoutStrict()
    {
        var c = Compare(Scenario(stimulusHash: "sha256:aaa"), Scenario(stimulusHash: null));

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Mismatch, Axis(c, "stimulus").State);
    }

    // ── A run with no facts at all — the pre-03242a1d shape ──────────────────

    [Fact]
    public void ARunWithNoComparabilityFacts_IsIncomparable_AndNoFlagRelaxesIt()
    {
        var bare = new ScenarioResult("s1", "s1", "in", "out", true, 0.5,
            new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0);

        var c = RunComparison.Of([bare], [Scenario()], strict: false);

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(ComparabilityAxisState.Unrecorded, Axis(c, "comparability").State);
        Assert.Equal(ComparisonVerdict.Incomparable, RunComparison.Of([bare], [bare]).Verdict);
    }

    // ── Set membership ───────────────────────────────────────────────────────

    [Fact]
    public void ScenariosPresentInOnlyOneRun_MakeTheComparisonIncomparable()
    {
        var c = RunComparison.Of(
            [Scenario("a"), Scenario("b")],
            [Scenario("a")]);

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Equal(["b"], c.BaselineOnly);
        Assert.Empty(c.CandidateOnly);
    }

    [Fact] // VACUITY: a comparison of nothing is not a comparison.
    public void TwoEmptyRuns_AreIncomparable_NotTriviallyComparable()
    {
        var c = RunComparison.Of([], []);

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Contains("A comparison of nothing", string.Join(" ", c.RefusalReasons), StringComparison.Ordinal);
    }

    [Fact] // VACUITY, second shape: two non-empty runs sharing no scenario id.
    public void RunsWithNoSharedScenarioId_AreIncomparable()
    {
        var c = RunComparison.Of([Scenario("a")], [Scenario("b")]);

        Assert.Equal(ComparisonVerdict.Incomparable, c.Verdict);
        Assert.Empty(c.Scenarios);
    }

    [Fact]
    public void ARepeatedScenarioId_Throws_RatherThanPickingOne()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RunComparison.Of([Scenario("a"), Scenario("a")], [Scenario("a")]));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => RunComparison.Of(null!, []));
        Assert.Throws<ArgumentNullException>(() => RunComparison.Of([], null!));
    }

    // ── The chance floor is REPORTED, never gating ───────────────────────────

    [Fact]
    public void NoChanceFloor_IsReported_AndDoesNotBlockTheComparison()
    {
        var c = Compare(Scenario(), Scenario());

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Single(c.ScenariosWithoutAFloor);
        Assert.DoesNotContain("chance", string.Join(" ", c.RefusalReasons), StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // …and a derived floor on both sides clears the report.
    public void AFloorOnBothSides_ClearsTheNoFloorReport()
    {
        var floor = RecordedChanceFloor.From(ChanceFloor.UniformChoice(4));
        var c = Compare(
            Scenario(comparability: Facts(floor: floor)),
            Scenario(comparability: Facts(floor: floor)));

        Assert.Empty(c.ScenariosWithoutAFloor);
    }

    [Fact] // A NotDerivable floor is not a usable bar, so the delta still has none.
    public void ANotDerivableFloor_DoesNotCountAsAFloor()
    {
        var floor = RecordedChanceFloor.From(ChanceFloor.NotDerivable("nothing to draw from"));
        var c = Compare(
            Scenario(comparability: Facts(floor: floor)),
            Scenario(comparability: Facts(floor: floor)));

        Assert.Single(c.ScenariosWithoutAFloor);
        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
    }

    [Fact] // A floor on one side only is not a comparability mismatch either — it is not an axis.
    public void AFloorOnOneSideOnly_IsNotAComparabilityMismatch()
    {
        var floor = RecordedChanceFloor.From(ChanceFloor.UniformChoice(4));
        var c = Compare(
            Scenario(comparability: Facts(floor: floor)),
            Scenario(comparability: Facts(floor: null)));

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Single(c.ScenariosWithoutAFloor);
    }

    // ── The judge-grades-itself finding — no positive specimen from a real run ──

    [Fact]
    // ⚠ NO RUN THIS REPOSITORY CAN PRODUCE REACHES THIS BRANCH. No shipped producer declares
    // EvalInput.SubjectModel, so every real run records JudgeSubjectRelation.Unknown. The branch is
    // exercised here on hand-built facts, and that limit is stated rather than papered over.
    public void SameModelJudge_IsSurfacedAsAFinding_AndDoesNotGate()
    {
        var self = Facts(judge: JudgeFingerprint.For("gpt-5.5", null, subjectModel: "gpt-5.5"));
        var c = Compare(Scenario(comparability: self), Scenario(comparability: self));

        Assert.Single(c.ScenariosWhereTheJudgeGradedItsOwnSubject);
        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
    }

    [Fact] // The negative direction, so the finding is not always-on.
    public void UnknownOrDifferentModelJudge_RaisesNoSelfGradingFinding()
    {
        var unknown = Facts(judge: JudgeFingerprint.For("gpt-5.5", null, subjectModel: null));
        var different = Facts(judge: JudgeFingerprint.For("gpt-5.5", null, subjectModel: "gpt-4o"));

        Assert.Empty(Compare(Scenario(comparability: unknown), Scenario(comparability: unknown))
            .ScenariosWhereTheJudgeGradedItsOwnSubject);
        Assert.Empty(Compare(Scenario(comparability: different), Scenario(comparability: different))
            .ScenariosWhereTheJudgeGradedItsOwnSubject);
    }

    // ── The deltas themselves ────────────────────────────────────────────────

    [Fact]
    public void Deltas_AreCandidateMinusBaseline()
    {
        var c = RunComparison.Of(
            [Scenario("a", score: 0.4, passed: false), Scenario("b", score: 0.9, passed: true)],
            [Scenario("a", score: 0.6, passed: true), Scenario("b", score: 0.5, passed: false)]);

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Equal(0.2, c.Scenarios[0].ScoreDelta, 12);
        Assert.Equal(-0.4, c.Scenarios[1].ScoreDelta, 12);
        Assert.Equal(-0.1, c.MeanScoreDelta, 12);
        Assert.Equal(1, c.Recovered);
        Assert.Equal(1, c.Regressed);
    }

    [Fact]
    public void GatingAxes_AreTheSevenNamed()
    {
        var c = Compare(Scenario(), Scenario());

        Assert.Equal(RunComparison.GatingAxes.Count, c.Scenarios.Single().Axes.Count);
        Assert.Equal(
            RunComparison.GatingAxes.OrderBy(a => a, StringComparer.Ordinal),
            c.Scenarios.Single().Axes.Select(a => a.Name).OrderBy(a => a, StringComparer.Ordinal));
    }
}
