// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>The outcome of <see cref="RunLedger.TryAdmitToolCall"/> — which budget (if any) was exceeded.</summary>
public enum RunBudgetDecision
{
    /// <summary>All budgets passed; the call was recorded.</summary>
    Admitted,

    /// <summary>The total tool-call budget for the run is exhausted.</summary>
    TotalExceeded,

    /// <summary>The per-tool call budget for this tool is exhausted.</summary>
    PerToolExceeded,

    /// <summary>The monetary budget for the run would be exceeded.</summary>
    MonetaryExceeded,
}

/// <summary>
/// Gatekeeper — a per-run <b>cross-hop accumulator</b>. A single tool body can't see state that spans the whole
/// orchestration (total calls, monetary sums, which ids were legitimately observed), so gates that need it read
/// it here. The ledger is the deterministic primitive the budget / referential-integrity / (future) taint gates
/// share instead of each keeping its own bookkeeping.
/// <para><b>Per-run scoped.</b> Keyed by the current <see cref="AgentRunScope"/> (established by the run gate), so
/// each run gets its own ledger and state never leaks across runs. Register <c>UseAgentEvalGate()</c> to
/// establish the scope. With no run scope present, all runs share one process-wide fallback ledger — so per-run
/// isolation requires the run scope.</para>
/// <para><b>Thread-safe.</b> All reads and mutations are serialized; the compound budget check + record is a
/// single atomic operation (<see cref="TryAdmitToolCall"/>) so it is correct under concurrent tool invocation.</para>
/// </summary>
public sealed class RunLedger
{
    // Weak, so a run's ledger is collected when the run scope is. The fallback key gives a single shared ledger
    // when there is no run scope (documented caveat — register the run gate for per-run isolation).
    private static readonly ConditionalWeakTable<object, RunLedger> PerRun = new();
    private static readonly object FallbackKey = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _monetary = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedIds = new(StringComparer.Ordinal);
    private int _totalToolCalls;

    /// <summary>The ledger for the current run (keyed by <see cref="AgentRunScope.Current"/>).</summary>
    public static RunLedger ForCurrentRun()
    {
        var key = (object?)AgentRunScope.Current ?? FallbackKey;
        return PerRun.GetValue(key, static _ => new RunLedger());
    }

    /// <summary>Total tool calls recorded across every tool this run.</summary>
    public int TotalToolCalls
    {
        get { lock (_lock) { return _totalToolCalls; } }
    }

    /// <summary>How many times a given tool has been recorded this run.</summary>
    public int ToolCallCount(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        lock (_lock)
        {
            return _toolCalls.TryGetValue(toolName, out var n) ? n : 0;
        }
    }

    /// <summary>The running sum of a named monetary argument (e.g. <c>"amount"</c>) recorded this run.</summary>
    public decimal MonetarySum(string argName)
    {
        ArgumentNullException.ThrowIfNull(argName);
        lock (_lock)
        {
            return _monetary.TryGetValue(argName, out var v) ? v : 0m;
        }
    }

    /// <summary>Whether an id was legitimately surfaced by an earlier tool this run (for referential-integrity checks).</summary>
    public bool WasObserved(string? id)
    {
        if (id is null)
        {
            return false;
        }

        lock (_lock)
        {
            return _observedIds.Contains(id);
        }
    }

    /// <summary>
    /// Atomically checks the run's budgets and, if all pass, records this call — a single critical section, so it
    /// is correct even when tool calls are invoked concurrently. A <paramref name="monetaryAmount"/> must be
    /// non-negative (the caller clamps it); a negative amount is never allowed to reduce the running sum.
    /// </summary>
    public RunBudgetDecision TryAdmitToolCall(
        string toolName, int? maxTotal, int? maxPerTool, string? monetaryArg, decimal monetaryAmount, decimal maxMonetary)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (monetaryAmount < 0m)
        {
            monetaryAmount = 0m;   // defensive: a negative amount must never manufacture budget headroom
        }

        lock (_lock)
        {
            if (maxTotal is int mt && _totalToolCalls >= mt)
            {
                return RunBudgetDecision.TotalExceeded;
            }

            var cur = _toolCalls.TryGetValue(toolName, out var n) ? n : 0;
            if (maxPerTool is int mp && cur >= mp)
            {
                return RunBudgetDecision.PerToolExceeded;
            }

            if (monetaryArg is not null)
            {
                var curMon = _monetary.TryGetValue(monetaryArg, out var v) ? v : 0m;
                if (curMon + monetaryAmount > maxMonetary)
                {
                    return RunBudgetDecision.MonetaryExceeded;
                }

                _monetary[monetaryArg] = curMon + monetaryAmount;
            }

            _toolCalls[toolName] = cur + 1;
            _totalToolCalls++;
            return RunBudgetDecision.Admitted;
        }
    }

    /// <summary>Records one call to <paramref name="toolName"/> (non-atomic; for general accounting).</summary>
    public void RecordToolCall(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        lock (_lock)
        {
            _toolCalls[toolName] = (_toolCalls.TryGetValue(toolName, out var n) ? n : 0) + 1;
            _totalToolCalls++;
        }
    }

    /// <summary>Marks an id as legitimately observed this run.</summary>
    public void RecordObservedId(string? id)
    {
        if (id is null)
        {
            return;
        }

        lock (_lock)
        {
            _observedIds.Add(id);
        }
    }
}
