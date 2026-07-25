// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that caps a run's <b>budget</b> off the shared <see cref="RunLedger"/> — a
/// denial-of-wallet / runaway-loop defense that no single tool body can enforce (cost accrues across the whole
/// orchestration). Blocks the next call once a configured budget is reached: total tool calls, per-tool call
/// count, or the running sum of a monetary argument.
/// <para><b>Per-run scoped</b> via <see cref="RunLedger"/> — register <c>UseAgentEvalGate()</c> so each run gets
/// its own budget. Pure-code, hot-path safe, and correct under concurrent tool invocation (the check + record is
/// one atomic ledger operation). A <b>negative</b> monetary amount can never reduce the running sum (it is
/// clamped to zero), so it cannot manufacture budget headroom. For real enforcement register it under
/// <c>ReplaceResult</c> / <c>Terminate</c>; under <c>WarnOnly</c> it only records breaches.</para>
/// </summary>
public sealed class RunBudgetGate : IToolGate
{
    private readonly int? _maxToolCalls;
    private readonly Dictionary<string, int>? _maxPerTool;
    private readonly string? _monetaryArg;
    private readonly decimal _maxMonetary;
    private readonly RunBudgetScope _budgetScope;

    /// <inheritdoc/>
    public string PolicyName => "RunBudgetGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public GateRequirements Requirements => GateRequirements.RunScope;

    /// <summary>Creates the gate. At least one budget must be set.</summary>
    /// <param name="maxToolCalls">Cap on total tool calls per run (blocks the call that would exceed it).</param>
    /// <param name="maxCallsPerTool">Per-tool call caps (e.g. <c>["delete_account"] = 1</c>).</param>
    /// <param name="maxMonetaryPerRun">Cap on the running sum of a monetary argument, e.g. <c>("amount", 1000m)</c>.</param>
    /// <param name="budgetScope">Which run's ledger to charge (P2-8). Defaults to <see cref="RunBudgetScope.Current"/>;
    /// pass <see cref="RunBudgetScope.Root"/> so a fan-out of nested sub-agent runs shares this one budget.</param>
    public RunBudgetGate(
        int? maxToolCalls = null,
        IReadOnlyDictionary<string, int>? maxCallsPerTool = null,
        (string argName, decimal max)? maxMonetaryPerRun = null,
        RunBudgetScope budgetScope = RunBudgetScope.Current)
    {
        _budgetScope = budgetScope;
        if (maxToolCalls is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "must be at least 1 when set.");
        }

        var hasPerTool = maxCallsPerTool is { Count: > 0 };
        if (maxToolCalls is null && !hasPerTool && maxMonetaryPerRun is null)
        {
            throw new ArgumentException("At least one budget (maxToolCalls, maxCallsPerTool, or maxMonetaryPerRun) is required.");
        }

        _maxToolCalls = maxToolCalls;
        if (hasPerTool)
        {
            _maxPerTool = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in maxCallsPerTool!)
            {
                if (kv.Value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxCallsPerTool), $"per-tool cap for '{kv.Key}' must be at least 1.");
                }

                _maxPerTool[kv.Key] = kv.Value;
            }
        }

        if (maxMonetaryPerRun is { } mon)
        {
            if (string.IsNullOrWhiteSpace(mon.argName))
            {
                throw new ArgumentException("maxMonetaryPerRun.argName must be non-empty.", nameof(maxMonetaryPerRun));
            }

            if (mon.max < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMonetaryPerRun), "monetary cap must be non-negative.");
            }

            _monetaryArg = mon.argName;
            _maxMonetary = mon.max;
        }
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var ledger = _budgetScope == RunBudgetScope.Root ? RunLedger.ForRootRun() : RunLedger.ForCurrentRun();

        var amount = 0m;
        if (_monetaryArg is not null)
        {
            switch (AmountArgumentParser.TryGetAmount(call.Arguments, _monetaryArg, out var raw))
            {
                case AmountParseResult.Unparseable:
                    // Present but unverifiable ⇒ fail closed (an unparseable amount must not slip under the cap).
                    return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                        $"monetary argument '{_monetaryArg}' is present but could not be parsed — cannot verify the run budget"));
                case AmountParseResult.Parsed:
                    amount = Math.Max(0m, raw);   // clamp: a negative amount can never create headroom under the cap
                    break;
                default:
                    break;   // Absent ⇒ this call has no monetary component
            }
        }

        var perToolCap = _maxPerTool is not null && _maxPerTool.TryGetValue(call.FunctionName, out var m) ? m : (int?)null;

        // Atomic check + record — correct under concurrent invocation.
        var decision = ledger.TryAdmitToolCall(call.FunctionName, _maxToolCalls, perToolCap, _monetaryArg, amount, _maxMonetary);

        var verdict = decision switch
        {
            RunBudgetDecision.TotalExceeded =>
                ToolGateVerdict.Block(PolicyName, $"run tool-call budget exhausted (max {_maxToolCalls} per run)"),
            RunBudgetDecision.PerToolExceeded =>
                ToolGateVerdict.Block(PolicyName, $"per-tool budget for '{call.FunctionName}' exhausted (max {perToolCap} per run)"),
            RunBudgetDecision.MonetaryExceeded =>
                ToolGateVerdict.Block(PolicyName, $"monetary budget for '{_monetaryArg}' exceeded (max {_maxMonetary.ToString(CultureInfo.InvariantCulture)} per run)"),
            _ => ToolGateVerdict.Allow(PolicyName),
        };

        return new ValueTask<ToolGateVerdict>(verdict);
    }
}
