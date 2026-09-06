// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>The outcome of an exact two-sided sign test on paired per-case deltas.</summary>
/// <param name="ArmA">Reference arm label.</param>
/// <param name="ArmB">Challenger arm label.</param>
/// <param name="Wins">Cases where B beat A.</param>
/// <param name="Losses">Cases where A beat B.</param>
/// <param name="Ties">Cases with no difference. Discarded from the test, which costs power.</param>
/// <param name="PValue">Exact two-sided p, or 1.0 when every case tied.</param>
/// <param name="MeanDelta">Mean of B - A over the paired cases.</param>
/// <param name="CiLow">Lower bound of the bootstrap 95% CI on the mean delta.</param>
/// <param name="CiHigh">Upper bound of the bootstrap 95% CI on the mean delta.</param>
/// <param name="MinimumAttainableP">
/// The smallest two-sided p this n could ever produce. When it exceeds 0.05 the test cannot reach
/// significance in principle, and the report says so instead of quoting a p-value as if it could.
/// </param>
/// <param name="Metric">Which channel was paired: <c>"recall"</c> (latent coverage) or <c>"precision@k"</c>.</param>
/// <param name="NotComparable">
/// Personas EXCLUDED from the pairing because the two arms presented different counts, or because
/// one side was silent — each with the two k's. Never a win, never a loss, never a tie: a pair at
/// unequal k is not a comparison of two answers, it is a comparison of two list lengths.
/// </param>
/// <param name="DeclaredK">
/// The budget both sides were cut to; 0 when paired k-blind or nothing was compared; -1 when the
/// budget was the live arm's own k and therefore differs persona by persona.
/// </param>
public sealed record SignTestOutcome(
    string ArmA,
    string ArmB,
    int Wins,
    int Losses,
    int Ties,
    double PValue,
    double MeanDelta,
    double CiLow,
    double CiHigh,
    double MinimumAttainableP,
    string Metric = "recall",
    IReadOnlyList<string>? NotComparable = null,
    int DeclaredK = 0)
{
    /// <summary>Non-tied pairs — the n the test actually ran on.</summary>
    public int EffectiveN => Wins + Losses;

    /// <summary>Pairs that were actually compared, ties included.</summary>
    public int ComparedN => Wins + Losses + Ties;

    /// <summary>True when the challenger won more cases than it lost. A DIRECTION, not a result.</summary>
    public bool ChallengerLeads => Wins > Losses;

    /// <summary>True when this n could never produce p &lt; 0.05, whatever the split.</summary>
    public bool UnderpoweredByConstruction => MinimumAttainableP > 0.05;

    /// <summary>The excluded pairs, never null.</summary>
    public IReadOnlyList<string> Excluded => NotComparable ?? [];

    /// <summary>
    /// True when NO pair could be compared at all. An undecidable comparison is not a pass for
    /// either side.
    /// </summary>
    public bool Undecidable => ComparedN == 0;
}

/// <summary>The channel a paired comparison is run on.</summary>
public enum CoverageMetric
{
    /// <summary>Latent coverage — recall over the gold tokens. Monotone in k.</summary>
    Recall,

    /// <summary>Precision@k — relevant items over the declared slots. Indifferent to k in expectation.</summary>
    PrecisionAtK,
}

/// <summary>
/// The paired comparison: per-case scores for every arm, the sign test, a bootstrap CI, and the
/// cost each arm ran up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reps are averaged into one observation per case BEFORE pairing.</b> The unit of analysis is
/// the case. Treating reps as independent observations is pseudo-replication and inflates
/// significance by a factor of sqrt(reps).
/// </para>
/// <para>
/// <b>This class never decides who won.</b> It computes a count, a p-value and an interval, and
/// hands them to the printer. The gate in Eval 02 is deliberately NOT "my architecture wins" —
/// gating on that creates an incentive to tune the eval until it does, which is the exact shape
/// of letting the artifact under test supply its own pass criterion.
/// </para>
/// </remarks>
public sealed class PairedCoverageReport
{
    private readonly Dictionary<(string PersonaId, string Arm), CoverageScore> _scores = [];
    private readonly Dictionary<(string PersonaId, string Arm), List<IReadOnlyList<PresentedCall>>> _presented = [];
    private readonly Dictionary<string, ArmCost> _costs = new(StringComparer.Ordinal);
    private readonly List<string> _armOrder = [];
    private readonly List<string> _personaOrder = [];

    /// <summary>Bootstrap resamples for the CI on the paired mean delta.</summary>
    public const int BootstrapResamples = 10_000;

    /// <summary>Fixed seed, so a re-run reproduces the interval exactly.</summary>
    public const int BootstrapSeed = 20260904;

    /// <summary>Arms recorded, in first-seen order.</summary>
    public IReadOnlyList<string> Arms => _armOrder;

    /// <summary>Personas recorded, in first-seen order.</summary>
    public IReadOnlyList<string> Personas => _personaOrder;

    /// <summary>Records one arm's per-case mean for one persona.</summary>
    /// <param name="personaId">Customer id.</param>
    /// <param name="arm">Arm label.</param>
    /// <param name="score">The rep-averaged score.</param>
    public void Record(string personaId, string arm, CoverageScore score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);

        if (!_armOrder.Contains(arm, StringComparer.Ordinal)) _armOrder.Add(arm);
        if (!_personaOrder.Contains(personaId, StringComparer.Ordinal)) _personaOrder.Add(personaId);
        _scores[(personaId, arm)] = score;
    }

    /// <summary>
    /// Records what one REPETITION of an arm actually presented, in the arm's own order, before
    /// any cut. The raw material every re-cut is made from.
    /// </summary>
    /// <remarks>
    /// The 2026-09-04 live run persisted per-cell means and nothing else, so its live cells cannot
    /// be re-cut at any k today — only compared, at the rounded k they were recorded at. Keeping
    /// the lists is what makes the NEXT paid run re-readable at any budget for free.
    /// </remarks>
    /// <param name="personaId">Customer id.</param>
    /// <param name="arm">Arm label.</param>
    /// <param name="presented">The rep's presented calls.</param>
    public void RecordPresented(string personaId, string arm, IReadOnlyList<PresentedCall> presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        ArgumentNullException.ThrowIfNull(presented);

        if (!_presented.TryGetValue((personaId, arm), out var reps))
            _presented[(personaId, arm)] = reps = [];
        reps.Add(presented);
    }

    /// <summary>Every recorded repetition's presented list for one cell, in rep order. Empty when none was recorded.</summary>
    /// <param name="personaId">Customer id.</param>
    /// <param name="arm">Arm label.</param>
    public IReadOnlyList<IReadOnlyList<PresentedCall>> PresentedRepsOf(string personaId, string arm) =>
        _presented.TryGetValue((personaId, arm), out var reps) ? reps : [];

    /// <summary>Accumulates one run's cost into an arm's total.</summary>
    /// <param name="arm">Arm label.</param>
    /// <param name="metrics">Harness performance metrics, or null for a no-LLM arm.</param>
    /// <param name="reachesAModel">
    /// Whether this arm issues chat-model calls at all. ⚠ <b>DECLARED by the caller (plan item
    /// 8.3), never inferred here.</b> The harness hands every arm a <c>PerformanceMetrics</c>
    /// object whether or not a model was involved, so <c>metrics is not null</c> does not answer
    /// this question — and a zero total answers it even less, because a genuine zero and an
    /// absent usage block are the same zero. <c>CoverageArm.ReachesAModel</c> is where it is known.
    /// Timing is still taken from <paramref name="metrics"/> for a model-free arm: how long the
    /// deterministic arms take is real information, and it is what the panel's footer talks about.
    /// </param>
    public void RecordCost(string arm, PerformanceMetrics? metrics, bool reachesAModel = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        if (!_costs.TryGetValue(arm, out var cost)) cost = new ArmCost();

        cost.Runs++;
        if (metrics is not null) cost.DurationMs += metrics.TotalDuration.TotalMilliseconds;

        if (!reachesAModel)
        {
            // ⚠ THIS IS THE ONLY PLACE THE TWO ZEROES ARE STILL DISTINGUISHABLE — plan item 8.3.
            // Once a model-free turn is folded into the running totals, an arm that genuinely spent
            // nothing and an arm whose usage never arrived are both `0 tokens, $0.0000`, and §55
            // forbids rendering those alike. Count the turns here so the printer can tell them
            // apart later.
            cost.ModelFreeRuns++;
        }
        else if (metrics is null)
        {
            // The arm DOES reach a model and the harness handed back no metrics at all. That is an
            // absence, not a zero, and it must read as one: count the turn as a model turn whose
            // usage never arrived, which puts the row on LOWER BOUND.
            cost.ModelRuns++;
            cost.RunsWithoutUsage++;
            cost.RunsWithoutCost++;
            cost.RunsWithoutModelId++;
        }
        else
        {
            cost.ModelRuns++;
            cost.PromptTokens += metrics.PromptTokens ?? 0;
            cost.CompletionTokens += metrics.CompletionTokens ?? 0;
            cost.EstimatedCost += metrics.EstimatedCost ?? 0m;

            // §60.2's lesson applied at the point of accumulation: "an absence is not a zero"
            // is about the HALVES of a usage block as well as the block. A response that reported
            // a prompt count and no completion count is a LOWER BOUND, not a total.
            bool hasPrompt = metrics.PromptTokens is not null;
            bool hasCompletion = metrics.CompletionTokens is not null;
            if (!hasPrompt && !hasCompletion) cost.RunsWithoutUsage++;
            else if (!hasPrompt || !hasCompletion) cost.RunsWithPartialUsage++;

            if (metrics.EstimatedCost is null) cost.RunsWithoutCost++;

            if (!string.IsNullOrWhiteSpace(metrics.ModelUsed)) cost.NoteModel(metrics.ModelUsed);
            else cost.RunsWithoutModelId++;
        }

        _costs[arm] = cost;
    }

    /// <summary>The cost totals for one arm.</summary>
    /// <param name="arm">Arm label.</param>
    public ArmCost CostOf(string arm) => _costs.TryGetValue(arm, out var c) ? c : new ArmCost();

    /// <summary>One arm's score for one persona, or null when it was not run.</summary>
    /// <param name="personaId">Customer id.</param>
    /// <param name="arm">Arm label.</param>
    public CoverageScore? ScoreOf(string personaId, string arm) =>
        _scores.TryGetValue((personaId, arm), out var s) ? s : null;

    /// <summary>
    /// Mean latent coverage for an arm over the SCORABLE cases only. Unscorable cases (empty gold)
    /// are excluded, never counted as zero or one.
    /// </summary>
    /// <param name="arm">Arm label.</param>
    public double MeanLatent(string arm)
    {
        var values = _personaOrder
            .Select(p => ScoreOf(p, arm))
            .Where(s => s is { IsScorable: true })
            .Select(s => s!.Value.Latent)
            .ToList();

        return values.Count == 0 ? double.NaN : values.Average();
    }

    /// <summary>How many personas contributed a scorable LATENT number for an arm.</summary>
    /// <remarks>
    /// A mean without its n is not a result. Printed beside every mean this class produces.
    /// </remarks>
    /// <param name="arm">Arm label.</param>
    public int LatentCount(string arm) =>
        _personaOrder.Count(p => ScoreOf(p, arm) is { IsScorable: true });

    /// <summary>Mean manifest coverage for an arm over the scorable cases.</summary>
    /// <param name="arm">Arm label.</param>
    public double MeanManifest(string arm)
    {
        var values = _personaOrder
            .Select(p => ScoreOf(p, arm))
            .Where(s => s is not null && !double.IsNaN(s.Value.Manifest))
            .Select(s => s!.Value.Manifest)
            .ToList();

        return values.Count == 0 ? double.NaN : values.Average();
    }

    /// <summary>
    /// How many personas contributed a defined MANIFEST number for an arm.
    /// </summary>
    /// <remarks>
    /// MEASURED on this corpus: only ONE persona (Sofia) has a leaf with two or more eligible
    /// purchases, so the "MEAN manifest" row is a mean over a single observation. Printing it
    /// without its n invites it to be read as a three-persona average.
    /// </remarks>
    /// <param name="arm">Arm label.</param>
    public int ManifestCount(string arm) =>
        _personaOrder.Count(p => ScoreOf(p, arm) is { } s && !double.IsNaN(s.Manifest));

    /// <summary>
    /// The cross-persona forced-choice rate for an arm: the share of personas whose own gold this
    /// arm's answer scored strictly highest on. Chance is exactly 1 / (scorable personas).
    /// </summary>
    /// <param name="arm">Arm label.</param>
    public double ForcedChoiceRate(string arm)
    {
        var values = _personaOrder
            .Select(p => ScoreOf(p, arm))
            .Where(s => s is not null && !double.IsNaN(s.Value.ForcedChoice))
            .Select(s => s!.Value.ForcedChoice)
            .ToList();

        return values.Count == 0 ? double.NaN : values.Average();
    }

    /// <summary>How many personas contributed a defined forced-choice outcome for an arm.</summary>
    /// <param name="arm">Arm label.</param>
    public int ForcedChoiceCount(string arm) =>
        _personaOrder.Count(p => ScoreOf(p, arm) is { } s && !double.IsNaN(s.ForcedChoice));

    /// <summary>
    /// The forced choice reduced to a <b>count of personas</b> — the only form an exact binomial
    /// can take as input — together with how many of those personas were SPLIT across their own
    /// repetitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Why this exists, and what it replaces (found by the stage-2 smoke, 2026-09-06).</b> A
    /// persona's forced-choice cell is <c>CoverageScore.Mean</c>'s average over that arm's
    /// repetitions, so on a multi-rep arm it takes values in {0, ⅓, ⅔, 1} and is <b>not a Bernoulli
    /// outcome</b>. The panel used to hand the exact test
    /// <c>(int)Math.Floor(meanOfCellMeans × personaCount)</c>, which is a count of nothing: on the
    /// stage-2 probe (n = 1 persona, 3 reps) the live arm printed <c>0.667 (0 of 1)</c> — a rate and
    /// a count that contradict each other on the same line — and on the shipped 12-persona paid
    /// cohort it printed <b>6 of 12</b> for a live arm whose cells say <b>7</b>.
    /// </para>
    /// <para>
    /// <b>The reduction, stated so it can be argued with.</b> A persona counts as a win iff the arm
    /// identified it on <b>more than half</b> of that persona's repetitions. A rep split down the
    /// middle is a LOSS, which is the same tie rule the forced choice already applies within a
    /// single answer ("a tie is a loss"). The unit of analysis stays the PERSONA:
    /// <c>CoverageScore.Mean</c> refuses to treat reps as independent trials — that is
    /// pseudo-replication and would inflate any significance claim by √reps — so this method must
    /// never be "re-fixed" by counting persona × rep.
    /// </para>
    /// <para>
    /// <b><paramref name="arm"/>'s split count is reported, never hidden.</b> Where cells are split,
    /// the majority tally and the mean rate are two different reductions of the same data and can
    /// disagree; the panel prints both and says how many cells were split, because silently showing
    /// one as if it were the other is the defect this method exists to remove.
    /// </para>
    /// </remarks>
    /// <param name="arm">Arm label.</param>
    /// <returns>
    /// <c>Wins</c> — personas identified on a majority of their reps; <c>Trials</c> — personas with a
    /// defined outcome (identical to <see cref="ForcedChoiceCount"/>); <c>Split</c> — personas whose
    /// cell is strictly between 0 and 1, i.e. whose reps disagreed.
    /// </returns>
    public (int Wins, int Trials, int Split) ForcedChoiceTally(string arm)
    {
        var values = _personaOrder
            .Select(p => ScoreOf(p, arm))
            .Where(s => s is not null && !double.IsNaN(s.Value.ForcedChoice))
            .Select(s => s!.Value.ForcedChoice)
            .ToList();

        return (
            Wins: values.Count(v => v > 0.5),
            Trials: values.Count,
            Split: values.Count(v => v > 0.0 && v < 1.0));
    }

    /// <summary>
    /// The personas whose forced-choice cell disagreed across repetitions for an arm, with the
    /// value, in persona order. Empty when every cell is a clean 0 or 1.
    /// </summary>
    /// <param name="arm">Arm label.</param>
    public IReadOnlyList<(string PersonaId, double Value)> ForcedChoiceSplitCells(string arm) =>
        [.. _personaOrder
            .Select(p => (PersonaId: p, Score: ScoreOf(p, arm)))
            .Where(x => x.Score is { } s && !double.IsNaN(s.ForcedChoice)
                     && s.ForcedChoice > 0.0 && s.ForcedChoice < 1.0)
            .Select(x => (x.PersonaId, Value: x.Score!.Value.ForcedChoice))];

    /// <summary>
    /// True when EVERY scorable persona's latent coverage cleared that persona's OWN floor for
    /// this arm, and at least one persona was scorable.
    /// </summary>
    /// <remarks>
    /// ⚠ The mean-to-mean form of this test is passed by an arm that is below the floor on two of
    /// three personas: MEASURED, a constant arm presenting one descaler to everybody scored
    /// 0.000 / 1.000 / 1.000 for a mean of 0.667 against a mean floor of 0.462 and PASSED, while
    /// being persona-blind by construction. One persona can carry a mean; it cannot carry this.
    /// </remarks>
    /// <param name="arm">Arm label.</param>
    public bool EveryPersonaAboveOwnFloor(string arm)
    {
        int scorable = 0;

        foreach (string persona in _personaOrder)
        {
            var score = ScoreOf(persona, arm);
            if (score is not { IsScorable: true }) continue;

            scorable++;
            if (score.Value.AboveOwnFloor is not true) return false;
        }

        return scorable > 0;
    }

    /// <summary>The personas for which this arm did NOT clear its own floor, for the report.</summary>
    /// <param name="arm">Arm label.</param>
    public IReadOnlyList<string> PersonasBelowOwnFloor(string arm) =>
    [
        .. _personaOrder.Where(p => ScoreOf(p, arm) is { IsScorable: true } s && s.AboveOwnFloor is not true)
    ];

    // ══ THE K-BLIND SIGN TEST IS GONE. ═══════════════════════════════════════════════════
    //
    // `SignTest(armA, armB)` paired per-case latent coverage while IGNORING how many items each
    // side presented. Coverage is recall and monotone in k, so it measured list length as much as
    // architecture. MEASURED twice, in both directions: on Eval 02's 2026-09-04 run it paired
    // 5-item controls against a 0–4-item live agent, and on Eval 09's 2026-09-05 run it paired a
    // workflow presenting 3–11 items (0 of 21 reps at k = 5) against an agent presenting exactly 5
    // on all 24. Its own docstring said it was "kept, unchanged, because Eval 09 still reads it".
    // Eval 09 no longer reads it, so there is no reason left to keep a method whose only property
    // is that it cannot refuse an incomparable pair.
    //
    // Everything pairs through SignTestAtEqualK below. NegativeControls.GraderSanity asserts that
    // no k-blind pairing method exists on this type, so re-introducing one goes red.

    /// <summary>
    /// Exact two-sided sign test over EQUAL-k pairs only. A persona whose two cells were scored at
    /// different presentation counts, or where either side was silent, is reported as NOT
    /// COMPARABLE and enters neither the count nor the p-value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Equal k means literally equal.</b> Both cells must carry the same <c>DeclaredK</c> (both
    /// cut to the same budget, or both at their own k), the same scored <c>PresentedCount</c>, and
    /// — because a rep-averaged count is a rounded mean — both must be uniform across their reps
    /// (<see cref="CoverageScore.KUniformAcrossReps"/>). A live arm whose three reps presented
    /// 5 / 5 / 4 has a rounded k of 5 and is NOT at equal k with a control at 5: one of its three
    /// observations was a 4-item answer.
    /// </para>
    /// <para>
    /// <b>A silent side is not comparable either.</b> Zero against anything is the absence of an
    /// answer beside an answer. It is listed, with its k's, so the reader sees exactly which
    /// personas the comparison could not reach — an n that quietly shrank would read as ties.
    /// </para>
    /// </remarks>
    /// <param name="armA">Reference arm.</param>
    /// <param name="armB">Challenger arm.</param>
    /// <param name="metric">Which channel to pair on.</param>
    public SignTestOutcome SignTestAtEqualK(string armA, string armB, CoverageMetric metric)
    {
        var deltas = new List<double>();
        var notComparable = new List<string>();
        var comparedKs = new List<int>();
        int wins = 0, losses = 0, ties = 0;

        foreach (string persona in _personaOrder)
        {
            var a = ScoreOf(persona, armA);
            var b = ScoreOf(persona, armB);

            // ⚠ PLAN ITEM 8.22. This used to be a bare `continue`, so a persona that one arm
            // scored and the other did not was dropped SILENTLY: not in Excluded, not in any
            // count, not in the printed NOT COMPARABLE list. The pairing's n quietly shrank and
            // the shrink was indistinguishable from there having been fewer personas — which is
            // the flattering direction, because a smaller n is a weaker test that still prints a
            // p-value. Newly load-bearing since Eval 09 started pairing on the declared-k report,
            // which can be missing cells the own-k report has.
            //
            // Both sides absent is NOT this pair's business — the persona ran in neither arm, and
            // listing it under every arm pair would be noise. One side present and the other not
            // is exactly the case the acceptance names, and it is now DECLARED.
            bool scorableA = a is { IsScorable: true };
            bool scorableB = b is { IsScorable: true };

            if (!scorableA || !scorableB)
            {
                if (scorableA || scorableB)
                {
                    notComparable.Add($"{persona} ({DescribeCell(armA, a)} vs {DescribeCell(armB, b)})");
                }

                continue;
            }

            CoverageScore sa = a.Value, sb = b.Value;

            if (sa.DeclaredK != sb.DeclaredK)
            {
                notComparable.Add($"{persona} (cut at k={sa.DeclaredK} vs k={sb.DeclaredK})");
                continue;
            }

            if (sa.IsSilent || sb.IsSilent)
            {
                notComparable.Add($"{persona} (SILENT: k {sa.PresentedCount} vs {sb.PresentedCount})");
                continue;
            }

            if (sa.PresentedCount != sb.PresentedCount || !sa.KUniformAcrossReps || !sb.KUniformAcrossReps)
            {
                string ua = sa.KUniformAcrossReps ? "" : "≈";
                string ub = sb.KUniformAcrossReps ? "" : "≈";
                notComparable.Add($"{persona} (k {ua}{sa.PresentedCount} vs {ub}{sb.PresentedCount})");
                continue;
            }

            double va = Select(sa, metric), vb = Select(sb, metric);
            if (double.IsNaN(va) || double.IsNaN(vb))
            {
                notComparable.Add($"{persona} ({metric} undefined on one side)");
                continue;
            }

            comparedKs.Add(sa.DeclaredK);
            double delta = vb - va;
            deltas.Add(delta);

            if (delta > 0) wins++;
            else if (delta < 0) losses++;
            else ties++;
        }

        // One budget across every compared pair → that k. A per-persona k (the own-k re-read,
        // where each persona was cut to ITS live count) → -1, which the printer renders as
        // "k_live (per persona)" rather than quoting whichever persona happened to come last.
        int declaredK = comparedKs.Count == 0 ? 0 : comparedKs.Distinct().Count() == 1 ? comparedKs[0] : -1;

        return Summarise(armA, armB, wins, losses, ties, deltas, metric, notComparable, declaredK);

        static double Select(CoverageScore s, CoverageMetric m) => m switch
        {
            CoverageMetric.Recall => s.Latent,
            CoverageMetric.PrecisionAtK => s.PrecisionAtK,
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Names a missing or unscorable cell for the NOT COMPARABLE list (plan item 8.22).
    /// </summary>
    /// <remarks>
    /// "No cell at all" and "a cell that could not be scored" are different facts with different
    /// remedies — the first is a run that did not reach the persona, the second is a run that did
    /// and got nothing usable — so they are not pooled into one word.
    /// </remarks>
    /// <param name="arm">The arm the cell belongs to.</param>
    /// <param name="score">The cell, or null when the arm recorded none.</param>
    private static string DescribeCell(string arm, CoverageScore? score) =>
        score is null                    ? $"{arm}: NO CELL"
      : score.Value.IsScorable           ? $"{arm}: scored"
                                         : $"{arm}: cell not scorable";

    private SignTestOutcome Summarise(
        string armA, string armB, int wins, int losses, int ties, List<double> deltas,
        CoverageMetric metric, List<string> notComparable, int declaredK)
    {
        int n = wins + losses;
        double p = ExactTwoSidedSignP(wins, n);
        double meanDelta = deltas.Count == 0 ? double.NaN : deltas.Average();
        var (low, high) = Bootstrap(deltas);

        // Computed from the NON-TIED count, which is the n the exact test actually runs on. Using
        // the full paired count would understate it: ties are discarded, and discarding them costs
        // power. At n = 1 the smallest attainable two-sided p is 1.0, i.e. no result is possible.
        double minimumAttainable = n == 0 ? 1.0 : Math.Min(1.0, 2.0 * Math.Pow(0.5, n));

        return new SignTestOutcome(
            armA, armB, wins, losses, ties, p, meanDelta, low, high, minimumAttainable,
            Metric: metric == CoverageMetric.Recall ? "recall" : "precision@k",
            NotComparable: notComparable,
            DeclaredK: declaredK);
    }

    /// <summary>
    /// Exact two-sided binomial p for <paramref name="wins"/> successes in n trials at p = 0.5:
    /// 2 x P(X &gt;= max(wins, n - wins)), clamped to 1.0. Returns 1.0 when n = 0 — every case tied,
    /// which is "no detectable difference", never a win.
    /// </summary>
    /// <param name="wins">Successes.</param>
    /// <param name="n">Non-tied trials.</param>
    public static double ExactTwoSidedSignP(int wins, int n)
    {
        if (n <= 0) return 1.0;

        int extreme = Math.Max(wins, n - wins);
        double tail = 0.0;
        for (int i = extreme; i <= n; i++) tail += BinomialCoefficient(n, i);
        tail /= Math.Pow(2.0, n);

        return Math.Min(1.0, 2.0 * tail);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n) return 0.0;
        k = Math.Min(k, n - k);
        double result = 1.0;
        for (int i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }

    private (double Low, double High) Bootstrap(IReadOnlyList<double> deltas)
    {
        if (deltas.Count == 0) return (double.NaN, double.NaN);

        var rng = new Random(BootstrapSeed);
        var means = new double[BootstrapResamples];

        for (int b = 0; b < BootstrapResamples; b++)
        {
            double sum = 0.0;
            for (int i = 0; i < deltas.Count; i++) sum += deltas[rng.Next(deltas.Count)];
            means[b] = sum / deltas.Count;
        }

        Array.Sort(means);
        int lowIndex = (int)Math.Floor(0.025 * BootstrapResamples);
        int highIndex = (int)Math.Ceiling(0.975 * BootstrapResamples) - 1;
        return (means[Math.Clamp(lowIndex, 0, BootstrapResamples - 1)],
                means[Math.Clamp(highIndex, 0, BootstrapResamples - 1)]);
    }

    /// <summary>Freezes the comparison into a serialisable snapshot.</summary>
    /// <remarks>
    /// ⚠ <paramref name="label"/> has NO default. It was a hard-coded "Eval 02 — Latent-Interest
    /// Coverage" string for as long as this method existed, so Eval 09's saved snapshot — a
    /// different eval, different arms, different question — carried Eval 02's name on disk
    /// (MEASUREMENT_STATUS §23.10, defect 4). A default would let the next caller inherit the same
    /// wrong name silently.
    /// </remarks>
    /// <param name="randomFloorByPersona">Per-persona random-draw floors, for the record.</param>
    /// <param name="label">What this comparison IS. The caller names itself; nothing here guesses.</param>
    public CoverageSnapshot ToSnapshot(IReadOnlyDictionary<string, double> randomFloorByPersona, string label) =>
        ToSnapshot(randomFloorByPersona, declaredK: 0, utterance: "", atDeclaredK: null, label);

    /// <summary>
    /// Freezes the comparison into a serialisable snapshot, with the declared-budget cut beside
    /// the own-k cells and every rep's presented SKUs, so the record can be re-cut later at any
    /// k without spending anything.
    /// </summary>
    /// <param name="randomFloorByPersona">Per-persona random-draw floors, for the record.</param>
    /// <param name="declaredK">The budget the utterance declared, or 0 when it declared none.</param>
    /// <param name="utterance">The customer utterance every arm was given, verbatim.</param>
    /// <param name="atDeclaredK">The same arms scored at the declared budget, or null.</param>
    /// <param name="label">What this comparison IS. The caller names itself; nothing here guesses.</param>
    public CoverageSnapshot ToSnapshot(
        IReadOnlyDictionary<string, double> randomFloorByPersona,
        int declaredK,
        string utterance,
        PairedCoverageReport? atDeclaredK,
        string label)
    {
        ArgumentNullException.ThrowIfNull(randomFloorByPersona);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return new CoverageSnapshot
        {
            Label = $"{label} (paired, n = {_personaOrder.Count})",
            PersonaCount = _personaOrder.Count,
            Arms = [.. _armOrder],
            DeclaredK = declaredK,
            Utterance = utterance ?? "",
            MeanLatentByArm = _armOrder.ToDictionary(a => a, MeanLatent, StringComparer.Ordinal),
            MeanManifestByArm = _armOrder.ToDictionary(a => a, MeanManifest, StringComparer.Ordinal),
            ForcedChoiceByArm = _armOrder.ToDictionary(a => a, ForcedChoiceRate, StringComparer.Ordinal),
            RandomFloorByPersona = new Dictionary<string, double>(randomFloorByPersona, StringComparer.Ordinal),
            Cells = [.. Cells(this, withSkus: true)],
            CellsAtDeclaredK = atDeclaredK is null ? [] : [.. Cells(atDeclaredK, withSkus: false)],
            CostByArm = _armOrder.ToDictionary(
                a => a,
                a => new ArmCostSnapshot(CostOf(a).Runs, (long)CostOf(a).DurationMs,
                                         CostOf(a).PromptTokens, CostOf(a).CompletionTokens,
                                         CostOf(a).EstimatedCost,
                                         CostOf(a).ModelFreeRuns, CostOf(a).ModelRuns,
                                         CostOf(a).RunsWithoutUsage, CostOf(a).RunsWithPartialUsage,
                                         CostOf(a).RunsWithoutCost, CostOf(a).RunsWithoutModelId,
                                         CostOf(a).ModelIds),
                StringComparer.Ordinal),
        };

        static IEnumerable<CoverageCellSnapshot> Cells(PairedCoverageReport source, bool withSkus) =>
            from persona in source._personaOrder
            from arm in source._armOrder
            let s = source.ScoreOf(persona, arm)
            where s is not null
            select new CoverageCellSnapshot(
                persona, arm,
                double.IsNaN(s.Value.Latent) ? -1.0 : s.Value.Latent,
                double.IsNaN(s.Value.Manifest) ? -1.0 : s.Value.Manifest,
                s.Value.LatentServed, s.Value.LatentTotal,
                s.Value.PresentedCount, s.Value.PhantomCount,
                s.Value.LatentFloor, s.Value.ForcedChoice,
                DeclaredK: s.Value.DeclaredK,
                PresentedBeforeCut: s.Value.PresentedBeforeCut,
                RelevantCount: s.Value.RelevantCount,
                PrecisionAtK: s.Value.PrecisionAtK,
                PrecisionFloor: s.Value.PrecisionFloor,
                KUniformAcrossReps: s.Value.KUniformAcrossReps,
                PresentedSkusByRep: withSkus
                    ? [.. source.PresentedRepsOf(persona, arm).Select(rep => rep.Select(c => c.Sku).ToList())]
                    : null);
    }

    /// <summary>How a cost row may be READ. Four states, and three of them are not a number.</summary>
    /// <remarks>
    /// <para>
    /// Plan item 8.3 filed this as <i>"rendering only — print — when ModelId is null"</i> and that
    /// is <b>unimplementable as written</b>: nothing on the snapshot carried a model id, so the
    /// printer could not tell a deterministic arm's true zero from a model arm whose usage never
    /// arrived. Both are <c>0 tokens · $0.0000</c>, and <c>MEASUREMENT_STATUS</c> §55 is the rule
    /// that says those two must never render alike. The fix is a state, derived from what the
    /// recorder actually saw, not a null check on a field that did not exist.
    /// </para>
    /// <para>
    /// This is the third sighting of the <i>absence-is-not-a-zero</i> shape in this repository —
    /// after the chat lane that spent and reported nothing (§55.1) and the meter that folded half a
    /// usage block in as a zero (§60.2, §61.7). Both of those were found by asking what an ABSENT
    /// input renders as; so was this one.
    /// </para>
    /// </remarks>
    public enum ArmCostState
    {
        /// <summary>The arm was never run. No claim about its cost is available at all.</summary>
        NotRun,

        /// <summary>
        /// Every run was recorded with NO metrics object — the caller's way of saying "this arm has
        /// no model". A genuine zero, and the only state in which zero may be printed as a number.
        /// </summary>
        NoModel,

        /// <summary>Every model run reported a complete usage block. The totals are totals.</summary>
        Measured,

        /// <summary>
        /// At least one model run reported no usage block, or only half of one. The totals are a
        /// LOWER BOUND and must never be rendered as though they were complete.
        /// </summary>
        LowerBound,
    }

    /// <summary>Running cost totals for one arm.</summary>
    public sealed class ArmCost
    {
        private readonly SortedSet<string> _modelIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many agent turns this arm ran.</summary>
        public int Runs { get; set; }

        /// <summary>Total wall clock, milliseconds.</summary>
        public double DurationMs { get; set; }

        /// <summary>Total prompt tokens reported by the provider.</summary>
        public int PromptTokens { get; set; }

        /// <summary>Total completion tokens reported by the provider.</summary>
        public int CompletionTokens { get; set; }

        /// <summary>Total estimated cost.</summary>
        public decimal EstimatedCost { get; set; }

        /// <summary>Turns this arm ran WITHOUT reaching a model — a deterministic arm's turns.</summary>
        public int ModelFreeRuns { get; set; }

        /// <summary>Turns this arm ran that DID reach a model, whatever the response reported.</summary>
        public int ModelRuns { get; set; }

        /// <summary>Model runs whose metrics carried NEITHER token count.</summary>
        public int RunsWithoutUsage { get; set; }

        /// <summary>Model runs whose metrics carried exactly ONE of the two token counts (§60.2).</summary>
        public int RunsWithPartialUsage { get; set; }

        /// <summary>Model runs whose metrics carried no <c>EstimatedCost</c>.</summary>
        public int RunsWithoutCost { get; set; }

        /// <summary>Model runs whose metrics named no model.</summary>
        public int RunsWithoutModelId { get; set; }

        /// <summary>Every distinct model id this arm's runs named, in order.</summary>
        public IReadOnlyList<string> ModelIds => [.. _modelIds];

        /// <summary>Records one run's model id.</summary>
        /// <param name="modelId">The provider's name for the model. Ignored when blank.</param>
        public void NoteModel(string? modelId)
        {
            if (!string.IsNullOrWhiteSpace(modelId)) _modelIds.Add(modelId.Trim());
        }

        /// <summary>
        /// How this row may be read. <b>Derived from what was RECORDED, never from the totals</b> —
        /// reading applicability out of the result is the defect §61.8 names, and a zero total is
        /// exactly the input that cannot answer this question.
        /// </summary>
        public ArmCostState State =>
            Runs == 0                                              ? ArmCostState.NotRun
            : ModelRuns == 0                                       ? ArmCostState.NoModel
            : RunsWithoutUsage > 0 || RunsWithPartialUsage > 0     ? ArmCostState.LowerBound
                                                                   : ArmCostState.Measured;
    }
}
