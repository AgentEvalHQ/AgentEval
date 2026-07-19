// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Counterfactual gate replay: "what would a DIFFERENT tool-gate configuration have done to this SAME
/// captured traffic?" Runs two named lists of the REAL <see cref="IToolGate"/> objects against the same
/// captured <see cref="GatedToolCall"/>s — not a simulation — so a divergence found here is exactly what
/// would have happened had the candidate config been live at capture time.
/// </summary>
/// <remarks>
/// Companion to <c>AgentEval.Cli.Infrastructure.LogFileReplayer</c> (<c>agenteval log-file replay</c>), which
/// answers "what would a DIFFERENT chat target have said to this traffic" by resending captured round-trips.
/// This answers the gate-configuration equivalent without any network call at all — tool gates are pure/bounded
/// by construction (<see cref="GateCost.PureCode"/>/<see cref="GateCost.Bounded"/>; <c>UseAgentEvalToolGate</c>
/// itself refuses <see cref="GateCost.Network"/>/<see cref="GateCost.Llm"/> gates inline), so replaying them
/// against already-captured tool calls needs nothing beyond the calls themselves.
/// </remarks>
public static class GateReplayer
{
    /// <summary>
    /// Runs <paramref name="baseline"/> and <paramref name="candidate"/> independently against every call in
    /// <paramref name="calls"/> and returns a per-call comparison. Each gate list is evaluated with the SAME
    /// sequential, first-Block/Mutate-wins semantics the real <c>UseAgentEvalToolGate</c> pipeline uses (see
    /// <see cref="EvaluateSequential"/>) — a gate list here behaves identically to how it would compose live.
    /// </summary>
    public static async Task<GateReplayComparison> CompareAsync(
        IReadOnlyList<GatedToolCall> calls,
        IReadOnlyList<IToolGate> baseline,
        IReadOnlyList<IToolGate> candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var rows = new List<GateReplayRow>(calls.Count);
        foreach (var call in calls)
        {
            var baselineResult = await EvaluateSequential(baseline, call, cancellationToken).ConfigureAwait(false);
            var candidateResult = await EvaluateSequential(candidate, call, cancellationToken).ConfigureAwait(false);

            var diverged = baselineResult.Action != candidateResult.Action;
            rows.Add(new GateReplayRow(call, baselineResult, candidateResult, diverged));
        }

        return new GateReplayComparison(rows);
    }

    /// <summary>
    /// Evaluates <paramref name="gates"/> in order against <paramref name="call"/> and returns the FIRST
    /// non-Allow verdict (Block or Mutate), or an Allow verdict if every gate allows — the same short-circuit
    /// contract <c>AgentEvalToolGateExtensions</c>'s live pipeline applies to a <c>foreach</c> over its own
    /// frozen gate list. An empty gate list allows (no gate to object).
    /// </summary>
    private static async ValueTask<ToolGateVerdict> EvaluateSequential(
        IReadOnlyList<IToolGate> gates, GatedToolCall call, CancellationToken cancellationToken)
    {
        foreach (var gate in gates)
        {
            var verdict = await gate.InspectAsync(call, cancellationToken).ConfigureAwait(false);
            if (verdict.Action != ToolGateAction.Allow)
            {
                return verdict;
            }
        }

        return ToolGateVerdict.Allow("(no gate objected)");
    }
}

/// <summary>One captured call's baseline-vs-candidate verdict comparison.</summary>
/// <param name="Call">The captured tool call both configs were evaluated against.</param>
/// <param name="Baseline">The effective verdict under the baseline gate list.</param>
/// <param name="Candidate">The effective verdict under the candidate gate list.</param>
/// <param name="Diverged">True when the two configs' effective <see cref="ToolGateAction"/> differ.</param>
public sealed record GateReplayRow(GatedToolCall Call, ToolGateVerdict Baseline, ToolGateVerdict Candidate, bool Diverged);

/// <summary>The full counterfactual replay result — one row per captured call, plus a convenience summary.</summary>
public sealed record GateReplayComparison(IReadOnlyList<GateReplayRow> Rows)
{
    /// <summary>Rows where the candidate configuration's effective action differs from the baseline's.</summary>
    public IReadOnlyList<GateReplayRow> Diverged => Rows.Where(r => r.Diverged).ToList();
}
