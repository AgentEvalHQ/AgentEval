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
/// A meta-gate (Phase 6, P6-1) that watches the run's ENFORCED-block volume — <see cref="RunLedger.TotalDenials"/>
/// (the F-B block-storm dimension) — rather than any single call. Once <c>threshold</c> enforced blocks have been
/// recorded this run, it blocks every subsequent call: repeated denials across a run are how probing looks (an agent
/// hammering many <c>ACTION_NOT_AUTHORIZED</c>s). It reads no arguments, so it is <see cref="GateCost.PureCode"/>.
/// <para><b>Escalate to Terminate.</b> A gate can't override the registration's <see cref="ToolGatePolicy"/>, so to
/// turn a block-storm into a run <i>halt</i> register this sentinel in its OWN <see cref="ToolGatePolicy.Terminate"/>
/// layer (a second <c>UseAgentEvalToolGate</c> call), leaving your ordinary gates on their own policy. Under a
/// non-terminating policy it still blocks each further probe.</para>
/// <para><b>Scope.</b> The tally is per-run (it reads <see cref="RunLedger.ForCurrentRun"/>, where enforced blocks are
/// recorded); a block-storm spread across separate nested sub-agent runs is each counted on its own run. With no run
/// scope the sentinel is a no-op (there is no per-run tally to read) — register the run gate for it to work.</para>
/// <para>The optional <paramref name="onBlockStorm"/> callback fires once, on the call the threshold is first crossed,
/// with an <see cref="BlockStormIncident"/> for alerting/incident routing; it is exception-isolated (an alert sink
/// must never break the gate).</para>
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

        var enforcedBlocks = RunLedger.ForCurrentRun().TotalDenials;
        if (enforcedBlocks < _threshold)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        // Alert once, on the transition (TotalDenials is monotonic and increments by at most one between the
        // sentinel's per-call checks, so it equals the threshold on exactly the call that first crosses it).
        if (enforcedBlocks == _threshold && _onBlockStorm is not null)
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
