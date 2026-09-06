// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

using System.Globalization;

/// <summary>How one comparability axis came out across two runs.</summary>
public enum ComparabilityAxisState
{
    /// <summary>Both runs recorded the fact and the two agree.</summary>
    Match = 0,

    /// <summary>
    /// Both runs recorded the fact and the two DIFFER, or exactly one of them recorded it. An
    /// asymmetry is a mismatch: "one run applied a bar and the other declared none" is a real
    /// difference in what was measured, not a gap in the record.
    /// </summary>
    Mismatch = 1,

    /// <summary>
    /// NEITHER run recorded the fact. This is not a match — nothing was shown — and it is not a
    /// mismatch either. It is a blind spot, counted and printed, and it is what <c>--strict</c>
    /// refuses on.
    /// </summary>
    Unpinned = 2,

    /// <summary>
    /// A run carries no <see cref="ComparabilityFacts"/> at all — the shape every run written before
    /// the facts existed has. Nothing about it can be compared, and no flag relaxes that.
    /// </summary>
    Unrecorded = 3,
}

/// <summary>The verdict of a run comparison.</summary>
public enum ComparisonVerdict
{
    /// <summary>Every gating axis matched. Deltas may be emitted.</summary>
    Comparable = 0,

    /// <summary>At least one gating axis did not match. Deltas are refused.</summary>
    Incomparable = 1,
}

/// <summary>One comparability axis, as measured across two runs.</summary>
/// <param name="Name">The axis — <c>evalKey</c>, <c>stimulus</c>, <c>judge.modelId</c>, …</param>
/// <param name="State">How it came out.</param>
/// <param name="Baseline">The baseline run's value, rendered, or null when it recorded none.</param>
/// <param name="Candidate">The candidate run's value, rendered, or null when it recorded none.</param>
/// <param name="Detail">One sentence a reader can act on.</param>
public sealed record ComparabilityAxis(
    string Name,
    ComparabilityAxisState State,
    string? Baseline,
    string? Candidate,
    string Detail);

/// <summary>One scenario, compared across two runs.</summary>
/// <param name="ScenarioId">The scenario id present in both runs.</param>
/// <param name="Axes">Every gating axis, in a fixed order.</param>
/// <param name="BaselineScore">The baseline score.</param>
/// <param name="CandidateScore">The candidate score.</param>
/// <param name="BaselinePassed">The baseline verdict.</param>
/// <param name="CandidatePassed">The candidate verdict.</param>
/// <param name="BaselineHasUsableFloor">Whether the baseline recorded a chance floor usable as a bar.</param>
/// <param name="CandidateHasUsableFloor">Whether the candidate recorded a chance floor usable as a bar.</param>
/// <param name="JudgeGradedItsOwnSubject">
/// True when either side recorded <see cref="JudgeSubjectRelation.SameModel"/> — the judge is the
/// subject's own model.
/// </param>
public sealed record ScenarioComparison(
    string ScenarioId,
    IReadOnlyList<ComparabilityAxis> Axes,
    double BaselineScore,
    double CandidateScore,
    bool BaselinePassed,
    bool CandidatePassed,
    bool BaselineHasUsableFloor,
    bool CandidateHasUsableFloor,
    bool JudgeGradedItsOwnSubject)
{
    /// <summary>Axes that block a comparison outright.</summary>
    public IReadOnlyList<ComparabilityAxis> Blocking =>
        [.. Axes.Where(a => a.State is ComparabilityAxisState.Mismatch or ComparabilityAxisState.Unrecorded)];

    /// <summary>Axes neither run pinned.</summary>
    public IReadOnlyList<ComparabilityAxis> Unpinned =>
        [.. Axes.Where(a => a.State is ComparabilityAxisState.Unpinned)];

    /// <summary>Candidate minus baseline. Meaningful only when the comparison is COMPARABLE.</summary>
    public double ScoreDelta => CandidateScore - BaselineScore;

    /// <summary>True when both runs recorded a floor a delta can be read against.</summary>
    public bool DeltaHasAFloor => BaselineHasUsableFloor && CandidateHasUsableFloor;
}

/// <summary>
/// <c>agenteval compare</c>'s decision, as a pure function of two runs' scenario results.
/// ADR-031 <b>S5</b> / plan Phase 7.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>What S5 asks for and what this refuses.</b> S5 is <i>"refusing to emit deltas across
/// incomparable runs (exit 13) rather than warning"</i>. Warnings get scrolled past; a delta that
/// should never have been printed gets pasted into a slide. So a blocked comparison here yields
/// <b>no deltas at all</b> — <see cref="Verdict"/> is <see cref="ComparisonVerdict.Incomparable"/>
/// and the renderer prints the axes that blocked it instead of numbers.
/// </para>
/// <para>
/// 🔴 <b>The first attempt at S5 was refuted for having ONE reachable outcome</b> — measured at
/// ADR-031 §0.1 Wave 7: five of V1's six comparability facts were recorded nowhere, so a
/// <c>compare</c> written to Phase 7.5's acceptance would have exited 13 on every pair of runs in
/// existence. That is why the axis model below has THREE states and not two. An axis NEITHER run
/// recorded is <see cref="ComparabilityAxisState.Unpinned"/>: a declared blind spot, counted and
/// printed, and blocking only under <paramref name="strict"/>. Treating it as a match would be the
/// silent-<c>{}</c> collapse ADR-030 §4.2 rejects; treating it as a mismatch would restore the
/// one-outcome defect. Both are refused, and the third state is the honest reading.
/// </para>
/// <para>
/// ⚠ <b>The chance floor is deliberately NOT a comparability axis.</b> V1 lists it among the six
/// facts a run must carry, and it is carried — but a floor answers <i>"is this score above
/// chance?"</i>, not <i>"did these two runs measure the same thing?"</i>. Two runs of one eval at one
/// version, one bar, one judge and one stimulus are comparable whether or not anybody derived a
/// floor. Gating on the floor would also make the success path unreachable in this repository, where
/// <b>no shipped eval records one</b>. So the floor is reported as an <b>interpretability</b> fact
/// against the DELTA — <see cref="ScenariosWithoutAFloor"/> — and never as a comparability verdict.
/// </para>
/// <para>
/// ⚠ <b><see cref="JudgeSubjectRelation"/> is not a gating axis either, and it has no positive
/// specimen.</b> No shipped producer declares <c>EvalInput.SubjectModel</c>, so every run this
/// repository can write records <see cref="JudgeSubjectRelation.Unknown"/>. Gating on it would be
/// gating on a field nothing sets. It is surfaced as a finding
/// (<see cref="ScenariosWhereTheJudgeGradedItsOwnSubject"/>) so that the day a producer does declare
/// one, a self-grading run is loud.
/// </para>
/// <para>
/// ⚠ <b>Vacuity.</b> Two runs with no scenario id in common produce
/// <see cref="ComparisonVerdict.Incomparable"/>, not a clean comparison over an empty set. So do two
/// empty runs. A comparison of nothing is not a comparison.
/// </para>
/// </remarks>
/// <param name="Scenarios">Every scenario present in BOTH runs, compared.</param>
/// <param name="BaselineOnly">Scenario ids the baseline run has and the candidate does not.</param>
/// <param name="CandidateOnly">Scenario ids the candidate run has and the baseline does not.</param>
/// <param name="Strict">Whether unpinned axes were treated as blocking.</param>
public sealed record RunComparison(
    IReadOnlyList<ScenarioComparison> Scenarios,
    IReadOnlyList<string> BaselineOnly,
    IReadOnlyList<string> CandidateOnly,
    bool Strict)
{
    /// <summary>The axes this comparison gates on, in render order.</summary>
    public static IReadOnlyList<string> GatingAxes { get; } =
        ["evalKey", "evalVersion", "effectiveBar", "judge", "judge.modelId", "judge.rubricDigest", "stimulus"];

    /// <summary>Scenarios carrying at least one blocking axis.</summary>
    public IReadOnlyList<ScenarioComparison> Blocked =>
        [.. Scenarios.Where(s => s.Blocking.Count > 0)];

    /// <summary>Scenarios carrying at least one unpinned axis.</summary>
    public IReadOnlyList<ScenarioComparison> WithUnpinnedAxes =>
        [.. Scenarios.Where(s => s.Unpinned.Count > 0)];

    /// <summary>
    /// Matched scenarios whose delta cannot be read against chance, because at least one side
    /// recorded no usable floor. Reported; never gating — see the type's remarks.
    /// </summary>
    public IReadOnlyList<ScenarioComparison> ScenariosWithoutAFloor =>
        [.. Scenarios.Where(s => !s.DeltaHasAFloor)];

    /// <summary>Matched scenarios where a judge graded its own subject's model.</summary>
    public IReadOnlyList<ScenarioComparison> ScenariosWhereTheJudgeGradedItsOwnSubject =>
        [.. Scenarios.Where(s => s.JudgeGradedItsOwnSubject)];

    /// <summary>The verdict. Deltas may be read only when this is <see cref="ComparisonVerdict.Comparable"/>.</summary>
    public ComparisonVerdict Verdict =>
        Scenarios.Count == 0
        || BaselineOnly.Count > 0
        || CandidateOnly.Count > 0
        || Blocked.Count > 0
        || (Strict && WithUnpinnedAxes.Count > 0)
            ? ComparisonVerdict.Incomparable
            : ComparisonVerdict.Comparable;

    /// <summary>Why the comparison was refused, one sentence per reason. Empty when comparable.</summary>
    public IReadOnlyList<string> RefusalReasons
    {
        get
        {
            var reasons = new List<string>();

            if (Scenarios.Count == 0)
            {
                reasons.Add(BaselineOnly.Count == 0 && CandidateOnly.Count == 0
                    ? "neither run contains a scenario. A comparison of nothing is not a comparison."
                    : "the two runs share NO scenario id. They did not measure the same cases.");
            }

            if (BaselineOnly.Count > 0)
            {
                reasons.Add($"{BaselineOnly.Count} scenario(s) are in the baseline only "
                          + $"({Join(BaselineOnly)}) — the runs did not measure the same set.");
            }

            if (CandidateOnly.Count > 0)
            {
                reasons.Add($"{CandidateOnly.Count} scenario(s) are in the candidate only "
                          + $"({Join(CandidateOnly)}) — the runs did not measure the same set.");
            }

            foreach (var scenario in Blocked)
            {
                foreach (var axis in scenario.Blocking)
                {
                    reasons.Add($"{scenario.ScenarioId} · {axis.Name}: {axis.Detail}");
                }
            }

            if (Strict)
            {
                foreach (var scenario in WithUnpinnedAxes)
                {
                    foreach (var axis in scenario.Unpinned)
                    {
                        reasons.Add($"{scenario.ScenarioId} · {axis.Name}: {axis.Detail} (--strict)");
                    }
                }
            }

            return reasons;
        }
    }

    /// <summary>
    /// The mean of every matched scenario's score delta. ⚠ Read it only when
    /// <see cref="Verdict"/> is <see cref="ComparisonVerdict.Comparable"/>; the renderer prints
    /// nothing derived from it otherwise.
    /// </summary>
    public double MeanScoreDelta =>
        Scenarios.Count == 0 ? double.NaN : Scenarios.Average(s => s.ScoreDelta);

    /// <summary>Matched scenarios the candidate passed and the baseline did not.</summary>
    public int Recovered => Scenarios.Count(s => s is { BaselinePassed: false, CandidatePassed: true });

    /// <summary>Matched scenarios the baseline passed and the candidate did not.</summary>
    public int Regressed => Scenarios.Count(s => s is { BaselinePassed: true, CandidatePassed: false });

    /// <summary>Compares two runs.</summary>
    /// <param name="baseline">The baseline run's scenario results.</param>
    /// <param name="candidate">The candidate run's scenario results.</param>
    /// <param name="strict">When true, an axis neither run pinned blocks the comparison.</param>
    /// <returns>The comparison.</returns>
    /// <exception cref="ArgumentNullException">When either list is null.</exception>
    /// <exception cref="ArgumentException">When either run repeats a scenario id.</exception>
    public static RunComparison Of(
        IReadOnlyList<ScenarioResult> baseline,
        IReadOnlyList<ScenarioResult> candidate,
        bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var left = Index(baseline, nameof(baseline));
        var right = Index(candidate, nameof(candidate));

        var shared = left.Keys.Intersect(right.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var scenarios = shared
            .Select(id => Compare(left[id], right[id]))
            .ToList();

        return new RunComparison(
            scenarios,
            [.. left.Keys.Except(right.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)],
            [.. right.Keys.Except(left.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)],
            strict);
    }

    private static Dictionary<string, ScenarioResult> Index(IReadOnlyList<ScenarioResult> results, string name)
    {
        var map = new Dictionary<string, ScenarioResult>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (!map.TryAdd(result.Id, result))
            {
                throw new ArgumentException(
                    $"Run '{name}' contains scenario id '{result.Id}' more than once. A comparison keyed on "
                  + "scenario id cannot silently pick one of two.", name);
            }
        }

        return map;
    }

    private static ScenarioComparison Compare(ScenarioResult a, ScenarioResult b)
    {
        var axes = new List<ComparabilityAxis>();

        ComparabilityFacts? fa = a.Comparability;
        ComparabilityFacts? fb = b.Comparability;

        if (fa is null || fb is null)
        {
            string which = (fa, fb) switch
            {
                (null, null) => "neither run",
                (null, _) => "the baseline run",
                _ => "the candidate run",
            };

            axes.Add(new ComparabilityAxis(
                "comparability", ComparabilityAxisState.Unrecorded,
                fa is null ? null : "recorded", fb is null ? null : "recorded",
                $"{which} recorded any comparability facts at all. A run written before those facts existed "
              + "cannot be shown comparable with anything, and no flag relaxes that."));
        }
        else
        {
            axes.Add(Text("evalKey", fa.EvalKey, fb.EvalKey,
                "the two runs graded different evals; a delta between them is a delta between different questions"));
            axes.Add(Text("evalVersion", fa.EvalVersion, fb.EvalVersion,
                "the same eval changed version between the runs; its rules may have changed with it"));
            axes.Add(Number("effectiveBar", fa.EffectiveBar, fb.EffectiveBar,
                "the two runs applied different pass bars, so their pass/fail verdicts are not the same measurement",
                "neither run recorded the bar it applied, so nothing shows the two used the same one"));

            axes.Add(Presence("judge", fa.Judge is not null, fb.Judge is not null,
                "one run was judged by a model and the other was deterministic",
                "neither run used a judge"));

            axes.Add(Text("judge.modelId", fa.Judge?.ModelId, fb.Judge?.ModelId,
                "two different judges graded these runs",
                "neither run used a judge, so there is no judge to differ"));
            axes.Add(Text("judge.rubricDigest", fa.Judge?.RubricDigest, fb.Judge?.RubricDigest,
                "the judge was given different rubrics; the same model against a different rubric is a different instrument",
                "neither run recorded a rubric digest, so nothing shows the judge was given the same rubric"));

            axes.Add(Stimulus(a.StimulusHash, b.StimulusHash));
        }

        bool selfGraded =
            fa?.Judge?.SubjectRelation == JudgeSubjectRelation.SameModel
            || fb?.Judge?.SubjectRelation == JudgeSubjectRelation.SameModel;

        return new ScenarioComparison(
            a.Id,
            axes,
            a.Score,
            b.Score,
            a.Passed,
            b.Passed,
            fa?.ChanceFloor?.IsUsableAsABar == true,
            fb?.ChanceFloor?.IsUsableAsABar == true,
            selfGraded);
    }

    // ── Axis constructors. Each encodes the same three-state rule, once. ─────────────────────

    private static ComparabilityAxis Text(
        string name, string? a, string? b, string mismatchDetail, string? unpinnedDetail = null)
    {
        bool hasA = !string.IsNullOrWhiteSpace(a);
        bool hasB = !string.IsNullOrWhiteSpace(b);

        if (!hasA && !hasB)
        {
            return new ComparabilityAxis(name, ComparabilityAxisState.Unpinned, null, null,
                unpinnedDetail ?? $"neither run recorded {name}");
        }

        if (hasA != hasB)
        {
            return new ComparabilityAxis(name, ComparabilityAxisState.Mismatch, a, b,
                $"only one run recorded {name} — an asymmetry is a difference, not a gap");
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            ? new ComparabilityAxis(name, ComparabilityAxisState.Match, a, b, "identical")
            : new ComparabilityAxis(name, ComparabilityAxisState.Mismatch, a, b, mismatchDetail);
    }

    private static ComparabilityAxis Number(
        string name, double? a, double? b, string mismatchDetail, string unpinnedDetail)
    {
        if (a is null && b is null)
            return new ComparabilityAxis(name, ComparabilityAxisState.Unpinned, null, null, unpinnedDetail);

        string? ra = a?.ToString("G17", CultureInfo.InvariantCulture);
        string? rb = b?.ToString("G17", CultureInfo.InvariantCulture);

        if (a is null || b is null)
        {
            return new ComparabilityAxis(name, ComparabilityAxisState.Mismatch, ra, rb,
                $"only one run recorded {name} — an eval that declared a bar and one that declared none are "
              + "not the same measurement");
        }

        // Exact equality on purpose: a tolerance here would hide a bar that actually moved.
        return a.Value.Equals(b.Value)
            ? new ComparabilityAxis(name, ComparabilityAxisState.Match, ra, rb, "identical")
            : new ComparabilityAxis(name, ComparabilityAxisState.Mismatch, ra, rb, mismatchDetail);
    }

    private static ComparabilityAxis Presence(
        string name, bool a, bool b, string mismatchDetail, string bothAbsentDetail)
    {
        if (a == b)
        {
            // Both-absent is a MATCH here, not unpinned: a null judge is a declared fact — "this
            // eval used no judge" — not a missing record. ComparabilityFacts says so explicitly.
            return new ComparabilityAxis(name, ComparabilityAxisState.Match,
                a ? "present" : "none", b ? "present" : "none",
                a ? "both runs were judged" : bothAbsentDetail);
        }

        return new ComparabilityAxis(name, ComparabilityAxisState.Mismatch,
            a ? "present" : "none", b ? "present" : "none", mismatchDetail);
    }

    private static ComparabilityAxis Stimulus(string? a, string? b)
    {
        if (a is null && b is null)
        {
            return new ComparabilityAxis("stimulus", ComparabilityAxisState.Unpinned, null, null,
                "neither run hashed its stimulus, so nothing shows the two were asked the same thing. "
              + "'Nobody computed a digest' is not 'the digests match'");
        }

        if (a is null || b is null)
        {
            return new ComparabilityAxis("stimulus", ComparabilityAxisState.Mismatch, a, b,
                "only one run hashed its stimulus; a null on either side is refused rather than read as agreement");
        }

        // The library's own rule decides, so this cannot drift from StimulusHash.SameStimulus.
        return StimulusHash.SameStimulus(a, b)
            ? new ComparabilityAxis("stimulus", ComparabilityAxisState.Match, a, b, "identical")
            : new ComparabilityAxis("stimulus", ComparabilityAxisState.Mismatch, a, b,
                "the two runs were asked different things");
    }

    private static string Join(IReadOnlyList<string> ids) =>
        ids.Count <= 5 ? string.Join(", ", ids) : string.Join(", ", ids.Take(5)) + $", … (+{ids.Count - 5})";
}
