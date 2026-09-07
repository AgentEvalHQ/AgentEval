// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.MafEvalFoundryAlongsideLocal;

/// <summary>
/// The query asked for a <b>3-day</b> plan. Did the answer lay out three days? One deterministic
/// leaf, admitted through AgentEval's floor-gated door, inside the composite the hybrid run scores.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this property.</b> The composite reaches this leaf through
/// <c>AgentEvalCompositeEvaluator</c>, whose projection is a two-field
/// <c>EvalInput(Query, Response)</c> (<c>AgentEvalCompositeEvaluator.cs:74</c>): no
/// <c>ToolCalls</c>, no <c>CaseId</c>. This agent has no tools anyway. So the leaf measures the one
/// thing the query itself states and the answer can be checked against without a judge: "3-day"
/// means three days, and a plan that never reaches day three did not answer the question asked.
/// </para>
/// <para>
/// 🔴 <b>Applicability comes from the QUERY, never from the answer.</b> The sample runs two queries
/// and only one asks for a day-numbered plan; the other ("the cheapest way from Paris to London")
/// has no day structure to check. Deciding applicability from the RESULT — "no day markers found,
/// so this must not have been an itinerary question" — is the silent-<c>{}</c> shape: a check that
/// excuses itself exactly when it would have failed. The trigger is read off
/// <see cref="EvalInput.Query"/> before the response is looked at.
/// </para>
/// <para>
/// <b>An echo of the query cannot pass this.</b> The query contains "3-day"; it contains none of
/// the nine day markers below. A check an arm satisfies by repeating its own input measures the
/// input.
/// </para>
/// <para>
/// ⚠ <b>Declared limitation.</b> This is a STRUCTURE test. An arm that knows nothing about Kyoto
/// and writes three numbered paragraphs of generic advice passes it. That is the honest ceiling of
/// what this door supports, and it is why the floor below is not derivable: the useful null is
/// "an arm that formats without understanding", and there is no draw model for that.
/// </para>
/// </remarks>
public sealed class ThreeDayItineraryEval()
    : AtomicCodeEval(EvalKey, "A 3-day request got three days", "agentic", "1.0.0")
{
    /// <summary>This eval's key, frozen in one place because the admission door refuses duplicates.</summary>
    public const string EvalKey = "maf.three_day_itinerary";

    /// <summary>Query spellings that make this eval applicable. Read from the QUERY, never the answer.</summary>
    private static readonly string[] s_triggers = ["3-day", "3 day", "three-day", "three day"];

    /// <summary>
    /// Accepted spellings per day, so the check survives ordinary prose variation. A response is
    /// credited with a day when ANY of that day's spellings appears; the three rows are independent,
    /// so "Day 1 … Day Two … the third day" still scores 3 of 3.
    /// </summary>
    private static readonly string[][] s_dayMarkers =
    [
        ["day 1", "day one", "first day"],
        ["day 2", "day two", "second day"],
        ["day 3", "day three", "third day"],
    ];

    /// <summary>
    /// The floor this leaf is admitted under: <b>not derivable</b>, and the reason is the finding.
    /// </summary>
    /// <remarks>
    /// A floor needs either a draw budget over a pool or a set of alternatives the task offers, and
    /// this door offers neither — the response is free text and the question is not a choice among N
    /// alternatives, so <c>ChanceFloor.UniformChoice(N)</c> has nothing to count. The null that would
    /// matter is "an arm that formats three numbered paragraphs without understanding Kyoto", and
    /// that arm has no nameable space to draw from. No number, with the reason recorded beside the
    /// verdict — because an absent floor rendered as 0.0 is a bar everything clears.
    /// </remarks>
    public static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "the MAF composite door supplies Query + Response only, and this is a structure test over free "
        + "text rather than a choice among alternatives, so UniformChoice has nothing to count. The null "
        + "worth clearing would be 'an arm that writes three numbered paragraphs without understanding "
        + "the destination' — that arm passes this check, and it has no draw model.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1 · Applicability, decided on the INPUT. A query that never asked for a 3-day plan has
        //     nothing here to get wrong, and a 0.0 against it would be a fabricated failure.
        var query = input.Query ?? string.Empty;
        if (!s_triggers.Any(t => query.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            var reason =
                "this query does not ask for a day-numbered plan, so there is no day structure to "
                + "check. Applicability is read off the query, never off the answer: deciding it from "
                + "the answer would let the check excuse itself exactly when it would have failed.";
            return NotApplicable(reason, new EvalEvidence("query", "not-a-3-day-request", reason));
        }

        // 2 · ⚠ NOT a fail. AgentEvalCompositeEvaluator.cs:74 does `response.Text ?? string.Empty`, so
        //     a run whose response was never captured and a run that genuinely said nothing arrive
        //     here as the SAME empty string. Scoring that 0.0 turns a blindness into a measurement.
        if (string.IsNullOrWhiteSpace(input.Response))
        {
            var reason =
                "the response reaching this leaf is empty, and through this door an empty response is "
                + "ambiguous: AgentEvalCompositeEvaluator collapses a missing response into \"\" (:74), "
                + "so 'nothing was captured' and 'the agent said nothing' are indistinguishable here. "
                + "An absent record is not an empty one.";
            return NotApplicable(reason, new EvalEvidence("response", "empty", reason));
        }

        var found = new List<int>();
        for (int day = 0; day < s_dayMarkers.Length; day++)
        {
            if (s_dayMarkers[day].Any(m => input.Response.Contains(m, StringComparison.OrdinalIgnoreCase)))
                found.Add(day + 1);
        }

        var passed = found.Count == s_dayMarkers.Length;
        var missing = Enumerable.Range(1, s_dayMarkers.Length).Except(found).ToList();

        var summary = passed
            ? "the answer lays out all three days the query asked for."
            : $"the query asked for a 3-day plan and the answer names {found.Count} of 3 days "
              + $"(missing: {string.Join(", ", missing.Select(d => $"day {d}"))}).";

        var scored = Build(
            value: (double)found.Count / s_dayMarkers.Length,
            passed: passed,
            severity: passed ? "none" : "medium",
            dimensions: null,
            evidence: [new EvalEvidence("response", "day-markers", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
