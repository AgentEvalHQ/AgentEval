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

        var baselineResults = await EvaluateConfigurationAsync(
            calls, baseline, "gate-replay-baseline", cancellationToken).ConfigureAwait(false);
        var candidateResults = await EvaluateConfigurationAsync(
            calls, candidate, "gate-replay-candidate", cancellationToken).ConfigureAwait(false);

        var rows = new List<GateReplayRow>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var baselineResult = baselineResults[i];
            var candidateResult = candidateResults[i];
            rows.Add(new GateReplayRow(
                calls[i],
                baselineResult,
                candidateResult,
                baselineResult.Action != candidateResult.Action));
        }

        return new GateReplayComparison(rows);
    }

    private static async Task<IReadOnlyList<ToolGateVerdict>> EvaluateConfigurationAsync(
        IReadOnlyList<GatedToolCall> calls,
        IReadOnlyList<IToolGate> gates,
        string scopeName,
        CancellationToken cancellationToken)
    {
        using var scope = AgentRunScope.BeginDetached(session: null, agentName: scopeName, trace: null);
        var results = new List<ToolGateVerdict>(calls.Count);
        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EvaluateSequential(gates, call, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Evaluates <paramref name="gates"/> in order against <paramref name="call"/> and returns the effective
    /// verdict, mirroring <c>AgentEvalToolGateExtensions</c>'s live pipeline EXACTLY (P2-4b / WM-4):
    /// <list type="bullet">
    /// <item>A <see cref="ToolGateAction.Block"/> short-circuits and wins immediately.</item>
    /// <item>A <see cref="ToolGateAction.Mutate"/> does NOT short-circuit — it rewrites the call's arguments and
    /// later gates evaluate against the mutated call, so a mutation that introduces a pattern an earlier-ordered
    /// gate ignored is seen by every gate after the mutator, just as it would be live. If no gate then blocks,
    /// the effective action is the (final) Mutate — distinct from a plain Allow because the call runs rewritten.</item>
    /// <item>A gate that THROWS fails closed to a synthetic Block (the live loop's cannot-inspect ⇒ deny rule) —
    /// it never propagates, which would abort the whole replay and silently drop every later captured call from
    /// the comparison.</item>
    /// <item>An arg-CHANGING Mutate re-validates from the top (P2-4 fixed point): a gate that would block the
    /// NEW arguments still gets its say, and the run fails closed if the arguments never converge. Revalidation
    /// passes skip <see cref="GateRequirements.RunScope"/> gates, exactly as the live loop does.</item>
    /// </list>
    /// An empty gate list allows (no gate to object).
    /// </summary>
    private static async ValueTask<ToolGateVerdict> EvaluateSequential(
        IReadOnlyList<IToolGate> gates, GatedToolCall call, CancellationToken cancellationToken)
    {
        const int maxMutationRevalidations = 8;
        var current = call;
        ToolGateVerdict? lastMutate = null;
        var revalidations = 0;
        var statefulGateInvoked = new bool[gates.Count];

        while (true)
        {
            var argumentsChanged = false;

            for (var gateIndex = 0; gateIndex < gates.Count; gateIndex++)
            {
                var gate = gates[gateIndex];

                // Parity with the live loop: a stateful RunScope gate runs exactly once (first reach) — re-running
                // it would double-charge the shared fallback ledger — but a stateful gate ordered AFTER a mutator
                // must still run once, so track per gate rather than blanket-skipping every revalidation pass.
                if (gate.Requirements.HasFlag(GateRequirements.RunScope))
                {
                    if (statefulGateInvoked[gateIndex])
                    {
                        continue;
                    }

                    statefulGateInvoked[gateIndex] = true;
                }

                ToolGateVerdict verdict;
                try
                {
                    verdict = await gate.InspectAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Mirror the live throw handler: a gate that throws cannot prove the call safe ⇒ Block.
                    return ToolGateVerdict.Block(gate.PolicyName, $"gate evaluation threw ({ex.GetType().Name}) — failing closed");
                }

                switch (verdict.Action)
                {
                    case ToolGateAction.Allow:
                        continue;

                    case ToolGateAction.Mutate:
                    {
                        // Rewrite the arguments (fully replaced, like the live Clear()+copy) only when the mutation
                        // actually CHANGES them — an idempotent/no-op Mutate is a fixed point, and re-scanning it
                        // would spin to the cap. Snapshot into an independent dictionary so a mutator aliasing the
                        // call's own arguments can't be corrupted (the replay analogue of P2-4a). GatedToolCall is
                        // immutable, so rebuild it.
                        if (verdict.NewArguments is not null
                            && !AgentEvalToolGateExtensions.ArgumentsEqual(current.Arguments, verdict.NewArguments))
                        {
                            current = current with { Arguments = new Dictionary<string, object?>(verdict.NewArguments) };
                            argumentsChanged = true;
                        }

                        lastMutate = verdict;
                        break;   // out of the switch; the post-switch check decides continue vs. revalidate
                    }

                    default:   // Block
                        return verdict;
                }

                if (argumentsChanged)
                {
                    break;   // restart the scan against the new arguments
                }
            }

            if (!argumentsChanged)
            {
                // No gate blocked and no arg-changing mutation this pass ⇒ fixed point. If any gate mutated, the
                // effective verdict is that (last) Mutate — the final rewritten arguments the tool would have
                // received; otherwise a plain Allow.
                return lastMutate ?? ToolGateVerdict.Allow("(no gate objected)");
            }

            if (++revalidations > maxMutationRevalidations)
            {
                return ToolGateVerdict.Block("MutationRevalidation",
                    $"tool-call arguments did not converge after {maxMutationRevalidations} mutation re-validations — failing closed");
            }
        }
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
