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
/// its own budget. Pure-code, hot-path safe. For real enforcement register it under <c>ReplaceResult</c> /
/// <c>Terminate</c>; under <c>WarnOnly</c> it only records breaches.</para>
/// </summary>
public sealed class RunBudgetGate : IToolGate
{
    private readonly int? _maxToolCalls;
    private readonly Dictionary<string, int>? _maxPerTool;
    private readonly string? _monetaryArg;
    private readonly decimal _maxMonetary;

    /// <inheritdoc/>
    public string PolicyName => "RunBudgetGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>
    /// Creates the gate. At least one budget must be set.
    /// </summary>
    /// <param name="maxToolCalls">Cap on total tool calls per run (blocks the call that would exceed it).</param>
    /// <param name="maxCallsPerTool">Per-tool call caps (e.g. <c>["delete_account"] = 1</c>).</param>
    /// <param name="maxMonetaryPerRun">Cap on the running sum of a monetary argument, e.g. <c>("amount", 1000m)</c>.</param>
    public RunBudgetGate(
        int? maxToolCalls = null,
        IReadOnlyDictionary<string, int>? maxCallsPerTool = null,
        (string argName, decimal max)? maxMonetaryPerRun = null)
    {
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

            _monetaryArg = mon.argName;
            _maxMonetary = mon.max;
        }
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var ledger = RunLedger.ForCurrentRun();

        // Check BEFORE recording, so maxToolCalls=N admits exactly N calls and blocks the (N+1)th.
        if (_maxToolCalls is int maxTotal && ledger.TotalToolCalls >= maxTotal)
        {
            return Blocked($"run tool-call budget exhausted (max {maxTotal} per run)");
        }

        if (_maxPerTool is not null && _maxPerTool.TryGetValue(call.FunctionName, out var perMax)
            && ledger.ToolCallCount(call.FunctionName) >= perMax)
        {
            return Blocked($"per-tool budget for '{call.FunctionName}' exhausted (max {perMax} per run)");
        }

        var thisAmount = 0m;
        if (_monetaryArg is not null && TryGetAmount(call.Arguments, _monetaryArg, out thisAmount)
            && ledger.MonetarySum(_monetaryArg) + thisAmount > _maxMonetary)
        {
            return Blocked($"monetary budget for '{_monetaryArg}' exceeded (max {_maxMonetary.ToString(CultureInfo.InvariantCulture)} per run)");
        }

        // Admitted — record this call in the shared ledger.
        ledger.RecordToolCall(call.FunctionName);
        if (thisAmount != 0m)
        {
            ledger.AddMonetary(_monetaryArg!, thisAmount);
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    private ValueTask<ToolGateVerdict> Blocked(string reason)
        => new(ToolGateVerdict.Block(PolicyName, reason));

    private static bool TryGetAmount(IReadOnlyDictionary<string, object?>? args, string argName, out decimal amount)
    {
        amount = 0m;
        if (args is null || !args.TryGetValue(argName, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case decimal d: amount = d; return true;
            case double db: amount = (decimal)db; return true;
            case float f: amount = (decimal)f; return true;
            case int i: amount = i; return true;
            case long l: amount = l; return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                amount = parsed; return true;
            default:
                try
                {
                    amount = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return false;
                }
        }
    }
}
