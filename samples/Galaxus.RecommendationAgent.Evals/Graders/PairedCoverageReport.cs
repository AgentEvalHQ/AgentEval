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
    public void RecordCost(string arm, PerformanceMetrics? metrics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        if (!_costs.TryGetValue(arm, out var cost)) cost = new ArmCost();

        cost.Runs++;
        if (metrics is not null)
        {
            cost.DurationMs += metrics.TotalDuration.TotalMilliseconds;
            cost.PromptTokens += metrics.PromptTokens ?? 0;
            cost.CompletionTokens += metrics.CompletionTokens ?? 0;
            cost.EstimatedCost += metrics.EstimatedCost ?? 0m;
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

    /// <summary>
    /// Exact two-sided sign test on the paired per-case LATENT deltas, plus a fixed-seed bootstrap
    /// CI on the mean delta — <b>k-blind</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This pairing ignores how many items each side presented.</b> MEASURED on Eval 02's
    /// 2026-09-04 live run, it paired a 5-item control against a 0–4-item live agent on a recall
    /// metric that is monotone in k, and reported the difference as architecture. Eval 02 no
    /// longer calls it; it pairs through <see cref="SignTestAtEqualK"/>, which refuses unequal-k
    /// pairs. It is kept, unchanged, because Eval 09 still reads it — and Eval 09's own review
    /// findings are that lane's to act on, not this method's to pre-empt by changing under it.
    /// </remarks>
    /// <param name="armA">Reference arm.</param>
    /// <param name="armB">Challenger arm.</param>
    public SignTestOutcome SignTest(string armA, string armB)
    {
        var deltas = new List<double>();
        int wins = 0, losses = 0, ties = 0;

        foreach (string persona in _personaOrder)
        {
            var a = ScoreOf(persona, armA);
            var b = ScoreOf(persona, armB);
            if (a is not { IsScorable: true } || b is not { IsScorable: true }) continue;

            double delta = b.Value.Latent - a.Value.Latent;
            deltas.Add(delta);

            if (delta > 0) wins++;
            else if (delta < 0) losses++;
            else ties++;
        }

        return Summarise(armA, armB, wins, losses, ties, deltas, CoverageMetric.Recall, [], declaredK: 0);
    }

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
            if (a is not { IsScorable: true } || b is not { IsScorable: true }) continue;

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
    /// <param name="randomFloorByPersona">Per-persona random-draw floors, for the record.</param>
    public CoverageSnapshot ToSnapshot(IReadOnlyDictionary<string, double> randomFloorByPersona) =>
        ToSnapshot(randomFloorByPersona, declaredK: 0, utterance: "", atDeclaredK: null);

    /// <summary>
    /// Freezes the comparison into a serialisable snapshot, with the declared-budget cut beside
    /// the own-k cells and every rep's presented SKUs, so the record can be re-cut later at any
    /// k without spending anything.
    /// </summary>
    /// <param name="randomFloorByPersona">Per-persona random-draw floors, for the record.</param>
    /// <param name="declaredK">The budget the utterance declared, or 0 when it declared none.</param>
    /// <param name="utterance">The customer utterance every arm was given, verbatim.</param>
    /// <param name="atDeclaredK">The same arms scored at the declared budget, or null.</param>
    public CoverageSnapshot ToSnapshot(
        IReadOnlyDictionary<string, double> randomFloorByPersona,
        int declaredK,
        string utterance,
        PairedCoverageReport? atDeclaredK)
    {
        ArgumentNullException.ThrowIfNull(randomFloorByPersona);

        return new CoverageSnapshot
        {
            Label = $"Eval 02 — Latent-Interest Coverage (paired, n = {_personaOrder.Count})",
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
                                         CostOf(a).EstimatedCost),
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

    /// <summary>Running cost totals for one arm.</summary>
    public sealed class ArmCost
    {
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
    }
}
