// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that caps <b>how many times a given tool may be called in one run</b> off the shared
/// <see cref="RunLedger"/> — a dedicated sibling of <see cref="RunBudgetGate"/>'s built-in per-tool cap, for when
/// you want a focused gate (its own <see cref="PolicyName"/> in the evidence trail) guarding only specific
/// high-blast-radius tools (e.g. <c>["delete_account"] = 1</c>, <c>["send_email"] = 3</c>) without also wiring an
/// overall run budget. Blunts loops and spray attacks — an injected instruction that tries to fire the same
/// destructive tool repeatedly is stopped at the configured count, regardless of how the model phrases each call.
/// <para><b>Per-run scoped</b> via <see cref="RunLedger"/> — register <c>UseAgentEvalGate()</c> so each run gets
/// its own counters. Pure-code, hot-path safe; the check + record is one atomic ledger operation
/// (<see cref="RunLedger.TryAdmitPerToolCall"/>), correct under concurrent tool invocation. A tool not named in
/// the configured caps is unconditionally allowed by this gate (pair with other gates for broader coverage).</para>
/// <para><b>Isolated ledger dimension.</b> This gate writes its own storage inside <see cref="RunLedger"/>,
/// separate from <see cref="RunBudgetGate"/>'s built-in <c>maxCallsPerTool</c> option — composing the two (even
/// over the same tool name) cannot double-count. Don't register <b>two</b> <see cref="PerToolCallBudgetGate"/>
/// instances capping the identical tool name in one chain; like stacking two <see cref="RunBudgetGate"/>s on one
/// tool, each would independently re-admit the same call into its own dimension's cap check — pick one gate per
/// tool you want to cap.</para>
/// </summary>
public sealed class PerToolCallBudgetGate : IToolGate
{
    private readonly Dictionary<string, int> _maxPerTool;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate over one or more per-tool call-count caps.</summary>
    /// <param name="maxCallsPerTool">Per-tool call caps (e.g. <c>["delete_account"] = 1</c>). Required, non-empty.</param>
    /// <param name="policyName">Optional override of the recorded policy name (default <c>"PerToolCallBudgetGate"</c>).</param>
    public PerToolCallBudgetGate(IReadOnlyDictionary<string, int> maxCallsPerTool, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(maxCallsPerTool);
        if (maxCallsPerTool.Count == 0)
        {
            throw new ArgumentException("At least one tool call-count cap is required.", nameof(maxCallsPerTool));
        }

        _maxPerTool = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in maxCallsPerTool)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                throw new ArgumentException("A guarded tool name must be non-empty.", nameof(maxCallsPerTool));
            }

            if (kv.Value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCallsPerTool), $"call-count cap for '{kv.Key}' must be at least 1.");
            }

            _maxPerTool[kv.Key] = kv.Value;
        }

        PolicyName = policyName ?? "PerToolCallBudgetGate";
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_maxPerTool.TryGetValue(call.FunctionName, out var cap))
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));   // not a guarded tool
        }

        var ledger = RunLedger.ForCurrentRun();
        var decision = ledger.TryAdmitPerToolCall(call.FunctionName, cap);

        var verdict = decision == RunBudgetDecision.PerToolExceeded
            ? ToolGateVerdict.Block(PolicyName, $"call budget for '{call.FunctionName}' exhausted (max {cap} per run)")
            : ToolGateVerdict.Allow(PolicyName);

        return new ValueTask<ToolGateVerdict>(verdict);
    }
}
