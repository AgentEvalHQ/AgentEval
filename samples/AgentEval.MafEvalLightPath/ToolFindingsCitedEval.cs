// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.MafEvalLightPath;

/// <summary>
/// Did the summary cite what the tools actually returned? One deterministic leaf, admitted through
/// the library's floor-gated door, sitting INSIDE the composite MAF runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this property and not a tool-call check.</b> The composite reaches this leaf through
/// <c>AgentEvalCompositeEvaluator</c>, whose projection is a two-field
/// <c>EvalInput(Query, Response)</c> (<c>AgentEvalCompositeEvaluator.cs:74</c>): no
/// <c>ToolCalls</c>, no <c>CaseId</c>. A tool-record check placed here would be
/// <see cref="MeasurementState.NotApplicable"/> on every run — true, and useless as a
/// demonstration. So this leaf measures the one thing that IS visible: the agent is instructed to
/// "ALWAYS call the provided tools, then summarise the best option", and both tools return a
/// CLOSED set of identifiers. A summary that cites none of them did not carry the tool findings
/// through.
/// </para>
/// <para>
/// 🔴 <b>The pool is the tools' own reply, not a copy of it.</b> <c>SearchFlights</c> and
/// <c>SearchHotels</c> build their reply strings FROM <see cref="FlightIds"/> and
/// <see cref="HotelNames"/>, so the set this eval looks for cannot drift from the set the agent was
/// actually shown. A checker whose pool has quietly diverged from reality is worse than no checker:
/// it fails honest arms and passes nothing.
/// </para>
/// <para>
/// <b>An echo of the query cannot pass this.</b> The query names Seattle, Paris and the Eiffel
/// Tower; it names no flight code and no hotel. A check an arm can satisfy by repeating its own
/// input measures the input, not the arm.
/// </para>
/// <para>
/// ⚠ <b>Declared limitation.</b> A fabricated citation that happens to collide with the pool
/// ("AA101" is a plausible invention) passes. This is a GROUNDING check over free text, not proof a
/// tool ran — that proof needs <c>EvalInput.ToolCalls</c>, which this door does not carry. The flat
/// lane's <c>ToolSelectionMetric</c> is where the call itself is checked.
/// </para>
/// </remarks>
public sealed class ToolFindingsCitedEval()
    : AtomicCodeEval(EvalKey, "Tool findings were cited", "travel", "1.0.0")
{
    /// <summary>This eval's key, frozen in one place because the admission door refuses duplicates.</summary>
    public const string EvalKey = "maf.tool_findings_cited";

    /// <summary>The flight identifiers <c>SearchFlights</c> returns — the tool's reply is built from this list.</summary>
    public static readonly IReadOnlyList<string> FlightIds = ["AA101", "DL205", "UA309"];

    /// <summary>The hotel names <c>SearchHotels</c> returns — the tool's reply is built from this list.</summary>
    public static readonly IReadOnlyList<string> HotelNames = ["Hotel Le Marais", "Ibis Paris", "Ritz Paris"];

    /// <summary>
    /// The floor this leaf is admitted under: <b>not derivable</b>, and the reason is the finding.
    /// </summary>
    /// <remarks>
    /// A floor answers "what would an arm that understood nothing score?", and that needs either a
    /// draw budget over a pool or a set of alternatives the task offers. This door offers neither:
    /// the response is free text, and the question is not a choice among N alternatives but a
    /// coverage test over two closed sets. <c>ChanceFloor.UniformChoice(N)</c> does not apply —
    /// naming one of three flights uniformly hits "one of the three" with certainty, so a
    /// uniform-choice null here would be a floor of 1.0 dressed up as 1/3. And the real null — an
    /// arm that fabricates a plausible flight code without calling the tool — has no nameable
    /// space to draw from. So: no number, with the reason recorded beside the verdict.
    /// </remarks>
    public static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "the MAF composite door supplies Query + Response only, and this is a coverage test over free "
        + "text rather than a choice among alternatives: no draw model exists for 'an arm that "
        + "fabricates a plausible flight code'. UniformChoice does not apply — drawing one of the "
        + "three flights uniformly satisfies 'cites one of the three' with certainty, which is a "
        + "ceiling of 1.0, not a floor of 1/3.");

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ⚠ NOT a fail. AgentEvalCompositeEvaluator.cs:74 does `response.Text ?? string.Empty`, so a
        // run whose response was never captured and a run that genuinely said nothing arrive here as
        // the SAME empty string. Scoring that 0.0 would turn a blindness into a measurement — the
        // same `?? 0` shape this lane exists to remove. Undecidable, with the reason.
        if (string.IsNullOrWhiteSpace(input.Response))
        {
            var reason =
                "the response reaching this leaf is empty, and through this door an empty response is "
                + "ambiguous: AgentEvalCompositeEvaluator collapses a missing response into \"\" "
                + "(:74), so 'nothing was captured' and 'the agent said nothing' are indistinguishable "
                + "here. An absent record is not an empty one.";
            return NotApplicable(reason, new EvalEvidence("response", "empty", reason));
        }

        var citedFlight = FlightIds.FirstOrDefault(
            id => input.Response.Contains(id, StringComparison.OrdinalIgnoreCase));
        var citedHotel = HotelNames.FirstOrDefault(
            name => input.Response.Contains(name, StringComparison.OrdinalIgnoreCase));

        var passed = citedFlight is not null && citedHotel is not null;

        // Half a mark per leg, because the query asked for two things and covering one is a real,
        // reportable partial result — not the same failure as covering neither.
        var value = (citedFlight is not null ? 0.5 : 0.0) + (citedHotel is not null ? 0.5 : 0.0);

        var summary = passed
            ? $"the summary cites flight '{citedFlight}' and hotel '{citedHotel}', both from what the tools returned."
            : citedFlight is null && citedHotel is null
                ? "the summary cites none of the three flights and none of the three hotels the tools returned."
                : citedFlight is null
                    ? $"the summary cites hotel '{citedHotel}' but none of the three flights the tools returned."
                    : $"the summary cites flight '{citedFlight}' but none of the three hotels the tools returned.";

        var scored = Build(
            value: value,
            passed: passed,
            severity: passed ? "none" : "medium",
            dimensions: null,
            evidence: [new EvalEvidence("response", "tool-findings", summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
