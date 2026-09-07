// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Benchmarks;

/// <summary>
/// Facts about runs, in meta-lane terms. Nothing here is an <see cref="IEval"/>, returns an
/// <see cref="EvalResult"/>, or writes a pass.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>The lane rule, and why it is worth a static class with no state.</b> ADR-030 §4.6: the
/// moment a meta type can return a result model, AgentEval has one more result model and it is the
/// one holding pass/fail authority. So these are pure functions from runs to
/// <see cref="FloorComparison"/> / <see cref="PairedComparison"/> / <see cref="ObservationCensus"/>,
/// every one of which is a described fact — successes, trials, a tail probability, a census — and
/// none of which is a verdict. Whoever decides whether a number is good enough decides it
/// somewhere else, with the census in front of them.
/// </para>
/// <para>
/// <b>The unit of analysis is the CASE, never the rep.</b> Several
/// <see cref="BenchmarkRun"/>s of one arm are repetitions, and they collapse per case BEFORE
/// anything is tested. Skipping that collapse is not a rounding nicety: it is
/// pseudo-replication, it inflates n by the rep count, and every interval computed from it is too
/// narrow by roughly √reps. The collapse is delegated to
/// <see cref="PairedEvalComparer.CollapseCell(string, string)"/> rather than re-implemented, because
/// it carries a rule an open-coded <c>ObservationUnit.Collapse</c> over the values would silently
/// drop: a cell whose reps are not ALL measured collapses to the WORST state present. Averaging the
/// reps that survived would quietly change the denominator and report an instrument failure as a
/// score.
/// </para>
/// <para>
/// <b>A floor at or above 1.0 is undecidable, and stays undecidable.</b>
/// <c>ExactTests.BinomialTailP</c> returns <see cref="double.NaN"/> for such a bar, and
/// <see cref="FloorComparison.AboveFloor"/> is then <see langword="false"/>. That is the honest
/// answer for a check luck cannot fail — it earns no significance and claims none. Nothing here
/// substitutes 0.5, clamps the bar, or turns the NaN into a pass.
/// </para>
/// </remarks>
public static class BenchmarkScore
{
    /// <summary>
    /// One arm against its own declared floor, per check, over every rep of that arm.
    /// </summary>
    /// <param name="runsOfOneArm">
    /// Reps of ONE arm against ONE definition. Every run must carry the same <c>ArmId</c> and the
    /// same definition key and version — a comparison across definitions is a different claim, and
    /// letting it through here would make it invisible.
    /// </param>
    /// <param name="collapse">How repetitions of the same case become one observation.</param>
    /// <param name="passAt">The value a rep must reach to count as a pass, for the pass-counting strategies.</param>
    /// <returns>One comparison per check, in the definition's check order.</returns>
    /// <exception cref="ArgumentException">
    /// The runs are empty, disagree on arm or definition, or a collapsed observation is neither 0
    /// nor 1 (which <see cref="FloorComparison.Compute"/> refuses rather than rounds).
    /// </exception>
    public static IReadOnlyList<(string CheckKey, FloorComparison Comparison)> AgainstFloor(
        IReadOnlyList<BenchmarkRun> runsOfOneArm,
        RepCollapse collapse = RepCollapse.All,
        double passAt = 1.0)
    {
        var definition = RequireOneArmOneDefinition(runsOfOneArm, nameof(runsOfOneArm));
        var armId = runsOfOneArm[0].ArmId;

        var results = new List<(string, FloorComparison)>(definition.Checks.Count);
        foreach (var check in definition.Checks)
        {
            var key = check.Eval.Key;
            var collapsed = CollapsePerCase(runsOfOneArm, key, collapse, passAt);
            results.Add((key, FloorComparison.Compute(collapsed, armId, check.Floor)));
        }

        return results;
    }

    /// <summary>
    /// A reference arm against a challenger, per check, with the case as the unit.
    /// </summary>
    /// <param name="reference">Reps of the arm being compared against.</param>
    /// <param name="challenger">Reps of the arm under test.</param>
    /// <param name="collapse">How repetitions of the same case become one observation.</param>
    /// <param name="passAt">The value a rep must reach to count as a pass, for the pass-counting strategies.</param>
    /// <returns>One comparison per check, in the definition's check order.</returns>
    /// <remarks>
    /// Every observation of BOTH arms is recorded into one <see cref="PairedEvalComparer"/> and the
    /// pairing happens inside it, so a case only one arm reached is excluded rather than counted as
    /// a tie. An undecidable scored as a tie is how "we could not look" becomes "no difference".
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Either side is empty or internally inconsistent, or the two sides were run against different
    /// definitions, or the two arms have the same id.
    /// </exception>
    public static IReadOnlyList<(string CheckKey, PairedComparison Comparison)> AgainstReference(
        IReadOnlyList<BenchmarkRun> reference,
        IReadOnlyList<BenchmarkRun> challenger,
        RepCollapse collapse = RepCollapse.All,
        double passAt = 1.0)
    {
        var referenceDefinition = RequireOneArmOneDefinition(reference, nameof(reference));
        var challengerDefinition = RequireOneArmOneDefinition(challenger, nameof(challenger));

        if (!SameDefinition(referenceDefinition, challengerDefinition))
        {
            throw new ArgumentException(
                $"The reference ran '{Describe(referenceDefinition)}' and the challenger ran "
                + $"'{Describe(challengerDefinition)}'. Two arms scored against different definitions are not "
                + "comparable, and a version bump means the cases or the checks changed — which is exactly "
                + "when the comparison silently stops meaning what it says.",
                nameof(challenger));
        }

        var referenceArm = reference[0].ArmId;
        var challengerArm = challenger[0].ArmId;
        if (string.Equals(referenceArm, challengerArm, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Both sides carry the arm id '{referenceArm}', so every pair would compare an arm with "
                + "itself and every case would tie. A comparison that cannot lose is not a comparison.",
                nameof(challenger));
        }

        var results = new List<(string, PairedComparison)>(referenceDefinition.Checks.Count);
        foreach (var check in referenceDefinition.Checks)
        {
            var key = check.Eval.Key;
            var comparer = new PairedEvalComparer(collapse, passAt);

            foreach (var observation in ObservationsOf(reference, key)) comparer.Record(observation);
            foreach (var observation in ObservationsOf(challenger, key)) comparer.Record(observation);

            results.Add((key, comparer.Compare(referenceArm, challengerArm)));
        }

        return results;
    }

    /// <summary>
    /// What went into each check's number: measured, not-applicable, not-measured.
    /// </summary>
    /// <param name="runsOfOneArm">Reps of ONE arm against ONE definition.</param>
    /// <returns>One census per check, in the definition's check order.</returns>
    /// <remarks>
    /// ⚠ <b>Counted over the COLLAPSED per-case observations, not the raw reps</b>, because the
    /// census exists to say what went into the number and the number is computed per case. A census
    /// over raw reps would report <c>cases × reps</c> as the denominator and make a 3-rep run look
    /// three times better powered than it is. An <see cref="ObservationCensus.Void"/> census means
    /// nothing was measurable — <b>not perfect, not zero.</b>
    /// </remarks>
    public static IReadOnlyList<(string CheckKey, ObservationCensus Census)> Census(
        IReadOnlyList<BenchmarkRun> runsOfOneArm)
    {
        var definition = RequireOneArmOneDefinition(runsOfOneArm, nameof(runsOfOneArm));

        var results = new List<(string, ObservationCensus)>(definition.Checks.Count);
        foreach (var check in definition.Checks)
        {
            var key = check.Eval.Key;

            // RepCollapse.All is the strictest collapse and is used here only to reach the STATE
            // rule inside CollapseCell; a census counts states, and no collapse strategy changes
            // which state a cell lands in.
            var collapsed = CollapsePerCase(runsOfOneArm, key, RepCollapse.All, passAt: 1.0);

            int measured = 0, notApplicable = 0, notMeasured = 0;
            foreach (var observation in collapsed)
            {
                switch (observation.State)
                {
                    case MeasurementState.NotApplicable: notApplicable++; break;
                    case MeasurementState.NotMeasured: notMeasured++; break;
                    default: measured++; break;
                }
            }

            results.Add((key, new ObservationCensus(measured, notApplicable, notMeasured)));
        }

        return results;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Collapses every rep of one check into one observation per case, via the comparer that already
    /// owns the worst-state rule.
    /// </summary>
    private static IReadOnlyList<Observation> CollapsePerCase(
        IReadOnlyList<BenchmarkRun> runs, string checkKey, RepCollapse collapse, double passAt)
    {
        var comparer = new PairedEvalComparer(collapse, passAt);
        var armId = runs[0].ArmId;

        // Case order comes from the DEFINITION, not from whichever run happened to be first: a run
        // that skipped a case must still appear as an absent cell rather than shift the others.
        var caseIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testCase in runs[0].Definition.Cases)
        {
            if (testCase.Id is { } id && seen.Add(id)) caseIds.Add(id);
        }

        foreach (var observation in ObservationsOf(runs, checkKey)) comparer.Record(observation);

        var collapsed = new List<Observation>(caseIds.Count);
        foreach (var caseId in caseIds)
        {
            if (comparer.CollapseCell(caseId, armId) is { } cell) collapsed.Add(cell);
        }

        return collapsed;
    }

    private static IEnumerable<Observation> ObservationsOf(IReadOnlyList<BenchmarkRun> runs, string checkKey)
    {
        foreach (var run in runs)
        {
            foreach (var observation in run.Observations)
            {
                if (string.Equals(observation.CheckKey, checkKey, StringComparison.Ordinal))
                    yield return observation.Observation;
            }
        }
    }

    private static BenchmarkDefinition RequireOneArmOneDefinition(
        IReadOnlyList<BenchmarkRun> runs, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(runs, parameterName);

        if (runs.Count == 0)
        {
            throw new ArgumentException(
                "No runs to score. An empty run set is not a zero score and not a clean sheet — it is a "
                + "benchmark that did not run, and scoring it would report that as a result.",
                parameterName);
        }

        var first = runs[0];
        ArgumentNullException.ThrowIfNull(first.Definition, parameterName);

        for (int i = 1; i < runs.Count; i++)
        {
            if (!string.Equals(runs[i].ArmId, first.ArmId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Run at index {i} carries arm '{runs[i].ArmId}' but the first carries '{first.ArmId}'. "
                    + "These are reps of ONE arm; pooling two arms here would collapse them into a single "
                    + "inflated n and hide the very difference a comparison exists to find.",
                    parameterName);
            }

            if (!SameDefinition(runs[i].Definition, first.Definition))
            {
                throw new ArgumentException(
                    $"Run at index {i} ran '{Describe(runs[i].Definition)}' but the first ran "
                    + $"'{Describe(first.Definition)}'. A version bump means the cases or the checks changed, "
                    + "so the two are not the same measurement.",
                    parameterName);
            }
        }

        return first.Definition;
    }

    private static bool SameDefinition(BenchmarkDefinition a, BenchmarkDefinition b) =>
        string.Equals(a.Key, b.Key, StringComparison.Ordinal)
        && string.Equals(a.Version, b.Version, StringComparison.Ordinal);

    private static string Describe(BenchmarkDefinition definition) => $"{definition.Key}@{definition.Version}";
}
