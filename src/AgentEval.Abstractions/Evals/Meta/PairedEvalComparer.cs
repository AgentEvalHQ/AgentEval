// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Evals.Meta;

/// <summary>
/// The result of pairing two arms case by case.
/// </summary>
/// <param name="Reference">The arm being compared against.</param>
/// <param name="Challenger">The arm under test.</param>
/// <param name="Wins">Cases where the challenger scored higher.</param>
/// <param name="Losses">Cases where the reference scored higher.</param>
/// <param name="Ties">Cases where they scored the same. Discarded by the test, counted here.</param>
/// <param name="PValue">Exact two-sided sign-test p over the non-tied pairs.</param>
/// <param name="MinimumAttainableP">The smallest p this design could ever have produced.</param>
/// <param name="MeanDelta">Mean (challenger − reference) over the compared pairs.</param>
/// <param name="Census">What went into the comparison, and what was excluded.</param>
/// <param name="Unit">The unit of analysis and the rep collapse that produced it.</param>
/// <param name="RuleHash">The pre-registered rule in force, stamped so a rule change is visible in a diff.</param>
/// <param name="Absent">Arms DECLARED absent, with their reasons. A declared absence renders; a missing one does not.</param>
public sealed record PairedComparison(
    string Reference,
    string Challenger,
    int Wins,
    int Losses,
    int Ties,
    double PValue,
    double MinimumAttainableP,
    double MeanDelta,
    ObservationCensus Census,
    ObservationUnit Unit,
    string? RuleHash,
    IReadOnlyDictionary<string, string> Absent)
{
    /// <summary>The n the exact test actually ran on: wins plus losses.</summary>
    public int EffectiveN => Wins + Losses;

    /// <summary>
    /// No observation at this n could have reached α. <b>A property of the DESIGN, not of the arms.</b>
    /// </summary>
    /// <remarks>
    /// The recorded instance: 9 ties left 4 informative pairs, so the minimum attainable two-sided p
    /// was 0.125 and the comparison could not have reached 0.05 even on a 4–0 sweep. Reporting the
    /// p-value without this beside it reads as "no difference found" when the truth is "no
    /// difference was findable".
    /// </remarks>
    public bool UnderpoweredByConstruction => MinimumAttainableP > ExactTests.DefaultAlpha;

    /// <summary>
    /// Nothing was comparable — every pair was refused or tied. <b>Not agreement.</b>
    /// </summary>
    public bool Undecidable => EffectiveN == 0;

    /// <summary>A DIRECTION, NOT A RESULT. Must never gate a build.</summary>
    public bool ChallengerLeads => Wins > Losses;

    /// <summary>One line a renderer can print without inventing a verdict.</summary>
    /// <returns>The comparison, rendered with its denominator and its power.</returns>
    public string Describe() =>
        Undecidable
            ? string.Create(CultureInfo.InvariantCulture,
                $"{Challenger} vs {Reference}: UNDECIDABLE — 0 comparable pairs ({Ties} tied, {Census.Describe()}).")
              + " A comparison that could not be made is not a comparison anybody won."
            : string.Create(CultureInfo.InvariantCulture,
                $"{Challenger} vs {Reference}: W/L/T {Wins}/{Losses}/{Ties}, p = {PValue:F4}, mean delta {MeanDelta:F4} ({Census.Describe()})")
              + (UnderpoweredByConstruction
                  ? string.Create(CultureInfo.InvariantCulture,
                      $" — UNDERPOWERED BY CONSTRUCTION: the smallest p this design could produce is {MinimumAttainableP:F4}")
                  : string.Empty);
}

/// <summary>
/// Records observations, collapses their repetitions, and pairs two arms case by case.
/// </summary>
/// <remarks>
/// <para>
/// ADR-030 §4.5. Rules baked in, each with a recorded reason:
/// </para>
/// <list type="bullet">
///   <item><description><b>Reps collapse before pairing.</b> There is no code path here that pairs raw reps.</description></item>
///   <item><description><b>Ties are discarded and counted</b>; the p-value is 1.0 when everything ties.</description></item>
///   <item><description><b>MinimumAttainableP comes from the non-tied n</b>, so an underpowered comparison says so.</description></item>
///   <item><description><b>A NotApplicable observation on either side EXCLUDES the pair.</b> It is never a loss and never a tie.</description></item>
///   <item><description><b>Bootstrap is not shipped.</b> At these n, Clopper-Pearson is exact and a bootstrap adds a seed to record and nothing else.</description></item>
/// </list>
/// <para>
/// <b>On pre-registration.</b> A <c>RegisteredAt</c> timestamp with a <c>RegisteredAfterData</c>
/// verdict is deliberately NOT here: it detects only within-process ordering, while the failure it
/// claims to prevent happens between runs, in an editor. A gate that catches the accident and not
/// the incentive provides false assurance. What survives is <see cref="PairedComparison.RuleHash"/>
/// — stamped into the artefact, so a rule change is visible in a diff across runs, which is the only
/// place it is actually detectable.
/// </para>
/// </remarks>
public sealed class PairedEvalComparer
{
    private readonly Dictionary<(string CaseId, string ArmId), List<Observation>> _reps = [];
    private readonly Dictionary<string, string> _absent = new(StringComparer.Ordinal);
    private readonly RepCollapse _collapse;
    private readonly double _passAt;

    /// <summary>Creates a comparer with a DECLARED rep-collapse strategy.</summary>
    /// <param name="collapse">How repetitions of the same (case, arm) become one observation.</param>
    /// <param name="passAt">The value a rep must reach to count as a pass, for the pass-counting strategies.</param>
    public PairedEvalComparer(RepCollapse collapse, double passAt = 1.0)
    {
        _collapse = collapse;
        _passAt = passAt;
    }

    /// <summary>The strategy this comparer was constructed with. Renderers must print it.</summary>
    public RepCollapse Collapse => _collapse;

    /// <summary>Records one repetition. Repetitions accumulate and are collapsed at compare time.</summary>
    /// <param name="observation">The observation to record.</param>
    public void Record(Observation observation)
    {
        var key = (observation.CaseId, observation.ArmId);
        if (!_reps.TryGetValue(key, out var list))
        {
            list = [];
            _reps[key] = list;
        }

        list.Add(observation);
    }

    /// <summary>
    /// Declares that an arm produced nothing, and why. <b>A declared absence renders; a missing arm
    /// does not.</b>
    /// </summary>
    /// <param name="armId">The absent arm.</param>
    /// <param name="reason">Why it is absent. Never empty.</param>
    public void DeclareAbsent(string armId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(armId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _absent[armId] = reason;
    }

    /// <summary>Collapses one (case, arm) cell to a single observation, or null when nothing was recorded.</summary>
    /// <param name="caseId">The case.</param>
    /// <param name="armId">The arm.</param>
    /// <returns>The collapsed observation, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A cell whose reps are not all measured collapses to the WORST state present — an
    /// instrument that did not run on one rep did not measure that case, and averaging over the
    /// reps that survived would silently change the denominator.
    /// </remarks>
    public Observation? CollapseCell(string caseId, string armId)
    {
        if (!_reps.TryGetValue((caseId, armId), out var reps) || reps.Count == 0) return null;

        foreach (var rep in reps)
        {
            if (rep.State == MeasurementState.NotMeasured) return Observation.NotMeasured(caseId, armId);
        }

        foreach (var rep in reps)
        {
            if (rep.State == MeasurementState.NotApplicable) return Observation.NotApplicable(caseId, armId);
        }

        var values = new double[reps.Count];
        for (int i = 0; i < reps.Count; i++) values[i] = reps[i].Value;

        return Observation.Measured(caseId, armId, ObservationUnit.Collapse(values, _collapse, _passAt));
    }

    /// <summary>Pairs two arms case by case and runs the exact sign test over the non-tied pairs.</summary>
    /// <param name="reference">The arm compared against.</param>
    /// <param name="challenger">The arm under test.</param>
    /// <param name="ruleHash">The pre-registered rule in force, stamped into the result.</param>
    /// <returns>The comparison. Never null, and never a verdict.</returns>
    public PairedComparison Compare(string reference, string challenger, string? ruleHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenger);

        int wins = 0, losses = 0, ties = 0;
        int measured = 0, notApplicable = 0, notMeasured = 0;
        double deltaSum = 0.0;
        int totalReps = 0;
        int cases = 0;

        foreach (string caseId in CaseIds())
        {
            var a = CollapseCell(caseId, reference);
            var b = CollapseCell(caseId, challenger);
            if (a is null || b is null) continue;

            cases++;
            totalReps += RepCount(caseId, reference) + RepCount(caseId, challenger);

            // ⚠ A NotApplicable or NotMeasured side EXCLUDES the pair. It is never a loss and never
            // a tie — scoring an undecidable as a tie is what makes "no difference" out of "we could
            // not look".
            if (a.Value.State != MeasurementState.Measured || b.Value.State != MeasurementState.Measured)
            {
                if (a.Value.State == MeasurementState.NotMeasured || b.Value.State == MeasurementState.NotMeasured)
                    notMeasured++;
                else
                    notApplicable++;
                continue;
            }

            measured++;
            double delta = b.Value.Value - a.Value.Value;
            deltaSum += delta;

            if (delta > 0.0) wins++;
            else if (delta < 0.0) losses++;
            else ties++;
        }

        int nonTied = wins + losses;

        return new(
            Reference: reference,
            Challenger: challenger,
            Wins: wins,
            Losses: losses,
            Ties: ties,
            PValue: ExactTests.TwoSidedSignP(wins, nonTied),
            MinimumAttainableP: ExactTests.MinimumAttainableP(nonTied),
            MeanDelta: measured == 0 ? double.NaN : deltaSum / measured,
            Census: new ObservationCensus(measured, notApplicable, notMeasured),
            Unit: new ObservationUnit(cases, totalReps, cases == 0 ? 0.0 : totalReps / (double)cases / 2.0, _collapse),
            RuleHash: ruleHash,
            Absent: new Dictionary<string, string>(_absent, StringComparer.Ordinal));
    }

    /// <summary>Every case id recorded, in the order it was first seen.</summary>
    /// <returns>The case ids.</returns>
    public IReadOnlyList<string> CaseIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var key in _reps.Keys)
        {
            if (seen.Add(key.CaseId)) order.Add(key.CaseId);
        }

        return order;
    }

    /// <summary>Every arm id recorded, plus every arm declared absent.</summary>
    /// <returns>The arm ids.</returns>
    public IReadOnlyList<string> ArmIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var key in _reps.Keys)
        {
            if (seen.Add(key.ArmId)) order.Add(key.ArmId);
        }

        foreach (string arm in _absent.Keys)
        {
            if (seen.Add(arm)) order.Add(arm);
        }

        return order;
    }

    private int RepCount(string caseId, string armId) =>
        _reps.TryGetValue((caseId, armId), out var reps) ? reps.Count : 0;
}
