// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

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

    /// <inheritdoc/>
    public string PolicyName => "RunBudgetGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate. At least one budget must be set.</summary>
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
        var ledger = RunLedger.ForCurrentRun();

        // Clamp so a negative amount can never create headroom under the cap.
        var amount = 0m;
        if (_monetaryArg is not null && TryGetAmount(call.Arguments, _monetaryArg, out var raw))
        {
            amount = Math.Max(0m, raw);
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
            case double db: return TryFromDouble(db, out amount);
            case float f: return TryFromDouble(f, out amount);
            case int i: amount = i; return true;
            case long l: amount = l; return true;
            case JsonElement je: return TryFromJsonElement(je, out amount);   // tool args often arrive as JsonElement
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
                    return false;   // not a usable amount ⇒ treat as no monetary component (never throw out of the gate)
                }
        }
    }

    private static bool TryFromJsonElement(JsonElement je, out decimal amount)
    {
        amount = 0m;
        return je.ValueKind switch
        {
            JsonValueKind.Number => je.TryGetDecimal(out amount),
            JsonValueKind.String => decimal.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out amount),
            _ => false,
        };
    }

    private static bool TryFromDouble(double d, out decimal amount)
    {
        amount = 0m;
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return false;
        }

        try
        {
            amount = (decimal)d;   // can overflow for very large magnitudes
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
