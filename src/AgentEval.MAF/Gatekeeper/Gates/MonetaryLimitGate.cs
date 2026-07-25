// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that caps the <b>running sum of a monetary argument across a run</b> off the shared
/// <see cref="RunLedger"/> — the economic sibling of <see cref="RunBudgetGate"/>, dedicated to payment / refund /
/// transfer-style tools. Blocks the call that would push the run's cumulative amount for a named argument (e.g.
/// <c>"amount"</c>) past a configured cap: no single tool body can see the running total, since it accrues across
/// every hop of the orchestration.
/// <para><b>Per-run scoped</b> via <see cref="RunLedger"/> — register <c>UseAgentEvalGate()</c> so each run gets
/// its own limit. Pure-code, hot-path safe; the check + record is one atomic ledger operation
/// (<see cref="RunLedger.TryAdmitMonetary"/>), correct under concurrent tool invocation. A <b>negative</b> amount
/// can never reduce the running sum (it is clamped to zero), so it cannot manufacture headroom under the cap. An
/// amount that is present but <b>unparseable</b> fails closed (blocks) rather than silently passing unchecked.</para>
/// <para><b>Evidence stays honest, never leaky.</b> The block reason names the argument and the <i>configured
/// cap</i> only — never the attempted amount or the run's running sum — so a blocked call's trace evidence cannot
/// be used to infer transaction values.</para>
/// <para><b>Isolated ledger dimension.</b> This gate writes its own storage inside <see cref="RunLedger"/>,
/// separate from <see cref="RunBudgetGate"/>'s built-in <c>maxMonetaryPerRun</c> option — composing the two (even
/// over the same argument name) cannot double-count. Don't register <b>two</b> <see cref="MonetaryLimitGate"/>
/// instances over the identical argument name in one chain; like stacking two <see cref="RunBudgetGate"/>s on one
/// argument, each would independently re-admit the same call's amount into its own dimension's cap check — pick
/// one gate per monetary dimension you want to enforce.</para>
/// </summary>
public sealed class MonetaryLimitGate : IToolGate
{
    private readonly string _argName;
    private readonly decimal _max;
    private readonly RunBudgetScope _budgetScope;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public GateRequirements Requirements => GateRequirements.RunScope;

    /// <summary>Creates the gate.</summary>
    /// <param name="argName">The tool-call argument that carries the monetary amount (e.g. <c>"amount"</c>).</param>
    /// <param name="maxTotal">The cap on the running sum of <paramref name="argName"/> across the run.</param>
    /// <param name="policyName">Optional override of the recorded policy name (default <c>"MonetaryLimitGate"</c>).</param>
    /// <param name="budgetScope">Which run's ledger to charge (P2-8). Defaults to <see cref="RunBudgetScope.Current"/>;
    /// pass <see cref="RunBudgetScope.Root"/> so a fan-out of nested sub-agent runs shares this one monetary cap.</param>
    public MonetaryLimitGate(string argName, decimal maxTotal, string? policyName = null, RunBudgetScope budgetScope = RunBudgetScope.Current)
    {
        if (string.IsNullOrWhiteSpace(argName))
        {
            throw new ArgumentException("argName must be non-empty.", nameof(argName));
        }

        if (maxTotal < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotal), "monetary cap must be non-negative.");
        }

        _argName = argName;
        _max = maxTotal;
        _budgetScope = budgetScope;
        PolicyName = policyName ?? "MonetaryLimitGate";
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        switch (AmountArgumentParser.TryGetAmount(call.Arguments, _argName, out var raw))
        {
            case AmountParseResult.Unparseable:
                // Present but unverifiable ⇒ fail closed (an unparseable amount must not slip under the cap).
                return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                    $"monetary argument '{_argName}' is present but could not be parsed — cannot verify the monetary limit"));

            case AmountParseResult.Absent:
                return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));   // no monetary component this call

            default:   // Parsed
                var amount = Math.Max(0m, raw);   // clamp: a negative amount can never create headroom under the cap
                var ledger = _budgetScope == RunBudgetScope.Root ? RunLedger.ForRootRun() : RunLedger.ForCurrentRun();
                var decision = ledger.TryAdmitMonetary(_argName, amount, _max);
                var verdict = decision == RunBudgetDecision.MonetaryExceeded
                    ? ToolGateVerdict.Block(PolicyName,
                        $"monetary limit for '{_argName}' exceeded (max {_max.ToString(CultureInfo.InvariantCulture)} per run)")
                    : ToolGateVerdict.Allow(PolicyName);
                return new ValueTask<ToolGateVerdict>(verdict);
        }
    }
}
