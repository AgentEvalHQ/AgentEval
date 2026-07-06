// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper — a per-run <b>cross-hop accumulator</b>. A single tool body can't see state that spans the whole
/// orchestration (total calls, monetary sums, which IDs were legitimately observed), so gates that need it read
/// it here. The ledger is the deterministic primitive the budget / referential-integrity / (future) taint gates
/// share instead of each keeping its own bookkeeping.
/// <para><b>Per-run scoped.</b> Keyed by the current <see cref="AgentRunScope"/> (established by the run gate), so
/// each run gets its own ledger and state never leaks across runs. Register <c>UseAgentEvalGate()</c> to
/// establish the scope. With no run scope present, all runs share one process-wide fallback ledger — so per-run
/// isolation requires the run scope.</para>
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

    /// <summary>The ledger for the current run (keyed by <see cref="AgentRunScope.Current"/>).</summary>
    public static RunLedger ForCurrentRun()
    {
        var key = (object?)AgentRunScope.Current ?? FallbackKey;
        return PerRun.GetValue(key, static _ => new RunLedger());
    }

    /// <summary>Total tool calls recorded across every tool this run.</summary>
    public int TotalToolCalls { get; private set; }

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
    public bool WasObserved(string id) => id is not null && ObservedContains(id);

    private bool ObservedContains(string id)
    {
        lock (_lock)
        {
            return _observedIds.Contains(id);
        }
    }

    /// <summary>Records one call to <paramref name="toolName"/>. Called by a gate as it admits a call.</summary>
    public void RecordToolCall(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        lock (_lock)
        {
            _toolCalls[toolName] = (_toolCalls.TryGetValue(toolName, out var n) ? n : 0) + 1;
            TotalToolCalls++;
        }
    }

    /// <summary>Adds <paramref name="amount"/> to the running sum for <paramref name="argName"/>.</summary>
    public void AddMonetary(string argName, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(argName);
        lock (_lock)
        {
            _monetary[argName] = (_monetary.TryGetValue(argName, out var v) ? v : 0m) + amount;
        }
    }

    /// <summary>Marks an id as legitimately observed this run.</summary>
    public void RecordObservedId(string id)
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
