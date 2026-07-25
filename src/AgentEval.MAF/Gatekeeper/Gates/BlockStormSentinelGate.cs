// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// One block-storm incident (Phase 6, P6-1): a run has accumulated enough ENFORCED policy blocks that the pattern
/// itself is the signal — an agent (or an attacker steering it) repeatedly trying denied actions is probing.
/// </summary>
/// <param name="RunId">The run this fired on (<see cref="AgentRunScope.RunId"/>).</param>
/// <param name="EnforcedBlockCount">Enforced blocks recorded this run when the sentinel tripped.</param>
/// <param name="Threshold">The configured trip threshold.</param>
/// <param name="Severity">Triage severity — <see cref="GateSeverity.Incident"/> (a block-storm demands attention).</param>
public sealed record BlockStormIncident(string? RunId, int EnforcedBlockCount, int Threshold, GateSeverity Severity);

/// <summary>
/// A meta-gate (Phase 6, P6-1) that watches the run tree's ENFORCED-block volume —
/// <see cref="RunLedger.TreeDenialCount"/> (the F-B block-storm dimension) — rather than any single call. Once
/// <c>threshold</c> enforced blocks have been recorded, it blocks every subsequent call: repeated denials are how
/// probing looks (an agent hammering many <c>ACTION_NOT_AUTHORIZED</c>s). It reads no arguments, so it is
/// <see cref="GateCost.PureCode"/>.
/// <para><b>Escalate to Terminate.</b> A gate can't override the registration's <see cref="ToolGatePolicy"/>, so a
/// block-storm becomes a run <i>halt</i> only when the sentinel is registered under
/// <see cref="ToolGatePolicy.Terminate"/> — add it to the SAME <c>UseAgentEvalToolGate</c> list as your other gates
/// (do NOT add a second, separate <c>UseAgentEvalToolGate</c> call: a later registration becomes OUTERMOST and can
/// silently starve the earlier one — see the warning on <c>UseAgentEvalToolGate</c>). Under a non-terminating policy
/// (Observe / ReplaceResult) the sentinel still BLOCKS each further probe but cannot itself force termination; put the
/// whole gate list on Terminate if you want a probing run stopped.</para>
/// <para><b>Scope.</b> The tally is aggregated at the run-tree ROOT (via <see cref="RunLedger.ForRootRun"/>), so a
/// block-storm spread across nested sub-agent runs still accumulates into one total and cannot be laundered under the
/// threshold — matching the P2-8 nested-run hardening the budget gates use. Separate <i>top-level</i> runs each have
/// their own tree and do not share. With no run scope the sentinel is a no-op (there is no tally to read) — register
/// the run gate for it to work.</para>
/// <para>The optional <paramref name="onBlockStorm"/> callback fires <b>exactly once per run tree</b>, the first time
/// the threshold is crossed (via an atomic latch, so it is race-free under concurrent tool calls), with an
/// <see cref="BlockStormIncident"/> for alerting/incident routing; it is exception-isolated (an alert sink must never
/// break the gate).</para>
/// </summary>
public sealed class BlockStormSentinelGate : IToolGate
{
    private readonly int _threshold;
    private readonly Action<BlockStormIncident>? _onBlockStorm;

    /// <inheritdoc/>
    public string PolicyName => "BlockStormSentinel";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the sentinel.</summary>
    /// <param name="threshold">Enforced blocks this run before the sentinel starts blocking. Default 5.</param>
    /// <param name="onBlockStorm">Optional incident callback, fired once on the trip (exception-isolated).</param>
    public BlockStormSentinelGate(int threshold = 5, Action<BlockStormIncident>? onBlockStorm = null)
    {
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "must be at least 1.");
        }

        _threshold = threshold;
        _onBlockStorm = onBlockStorm;
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var scope = AgentRunScope.Current;
        if (scope is null)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));   // no per-run tally to read
        }

        // Tree-wide total (ForRootRun) so a storm can't be laundered across nested sub-runs. Blocking is monotonic
        // (>= threshold), so it never fails open even if the tally jumps past the exact threshold value.
        var enforcedBlocks = RunLedger.ForRootRun().TreeDenialCount;
        if (enforcedBlocks < _threshold)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        // Alert exactly once per run tree via an atomic latch — race-free under concurrent tool calls and correct
        // even when the tally jumps past the threshold (a naive "== threshold" check would miss or double-fire).
        if (_onBlockStorm is not null && RunLedger.ForRootRun().TryLatchBlockStorm())
        {
            try
            {
                _onBlockStorm(new BlockStormIncident(scope.RunId, enforcedBlocks, _threshold, GateSeverity.Incident));
            }
            catch
            {
                // An alert sink must never break the gate (Phase 3 exception-isolation discipline).
            }
        }

        // Reason is audit-only (trace evidence); the model sees only the non-revealing {referenceId} refusal body (#12).
        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
            $"block-storm: {enforcedBlocks} enforced policy blocks this run (threshold {_threshold}) — repeated denials indicate probing"));
    }
}
