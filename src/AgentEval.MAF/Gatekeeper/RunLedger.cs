// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A per-tool result-SIZE snapshot from <see cref="RunLedger.RecordToolResultSize"/> — the running state AS IT
/// WAS BEFORE the call that produced it was recorded (the baseline to compare that call's size against, not
/// including it). Consumed by <see cref="ToolResultSizeAnomalyGate"/>.
/// </summary>
/// <param name="Count">Prior calls to this tool recorded this run.</param>
/// <param name="Sum">Sum of those prior calls' result sizes (characters).</param>
/// <param name="Max">Largest of those prior calls' result sizes.</param>
public readonly record struct ToolResultSizeSnapshot(int Count, long Sum, long Max)
{
    /// <summary>Mean result size over <see cref="Count"/> prior calls to this tool this run. 0 when <see cref="Count"/> is 0 (no baseline yet).</summary>
    public double AverageSize => Count == 0 ? 0.0 : (double)Sum / Count;
}

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
/// orchestration (total calls, per-tool counts, monetary sums), so the <see cref="RunBudgetGate"/> reads it here
/// instead of keeping its own bookkeeping. (The observed-id members below are a related primitive the flow-control
/// gates no longer use — <c>ReferentialIntegrityGate</c> and <c>TaintTrackingGate</c> recompute statelessly from the
/// run history per call — kept for a custom cross-hop check; see <see cref="WasObserved"/>.)
/// <para><b>Per-run scoped.</b> Keyed by the current <see cref="AgentRunScope"/> (established by the run gate), so
/// each run gets its own ledger and state never leaks across runs. Register <c>UseAgentEvalGate()</c> to
/// establish the scope. With no run scope present, all runs share one process-wide fallback ledger — so per-run
/// isolation requires the run scope.</para>
/// <para><b>Thread-safe.</b> All reads and mutations are serialized; the compound budget check + record is a
/// single atomic operation (<see cref="TryAdmitToolCall"/>) so it is correct under concurrent tool invocation.</para>
/// <para><b>Dimension isolation (<see cref="MonetaryLimitGate"/> / <see cref="PerToolCallBudgetGate"/>).</b>
/// <see cref="TryAdmitToolCall"/> ALWAYS records a tool-call-count increment on admit — even for a budget
/// dimension the caller didn't ask it to check — so a second gate sharing the same <c>_toolCalls</c>/<c>_monetary</c>
/// storage would silently double-count calls <see cref="RunBudgetGate"/> also happens to see. <see cref="TryAdmitMonetary"/>
/// and <see cref="TryAdmitPerToolCall"/> therefore write to their <b>own, isolated</b> storage — composing
/// <see cref="RunBudgetGate"/> with either dedicated gate (even over overlapping tools/argument names) can never
/// cross-contaminate either gate's count. The one caveat that remains (as with any two gates guarding the exact
/// same dimension) is registering <b>two instances of the same dedicated gate</b> over the identical argument/tool
/// name — that is a self-inflicted double-count, same as stacking two <see cref="RunBudgetGate"/>s on one argument.</para>
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

    // Isolated storage for the dedicated sibling gates (MonetaryLimitGate / PerToolCallBudgetGate) — deliberately
    // NOT the same dictionaries TryAdmitToolCall writes, so composing them with RunBudgetGate (or with each other)
    // over an overlapping tool/argument name cannot cross-contaminate either gate's count. See the class doc.
    private readonly Dictionary<string, decimal> _monetaryLimitSums = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _perToolCallBudgetCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Count, long Sum, long Max)> _toolResultSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _denials = new(StringComparer.Ordinal);   // P4-3: per-(tool+arg-shape) denial tally (F-B dimension)

    /// <summary>The ledger for the current run (keyed by <see cref="AgentRunScope.Current"/>).</summary>
    public static RunLedger ForCurrentRun()
    {
        var key = (object?)AgentRunScope.Current ?? FallbackKey;
        return PerRun.GetValue(key, static _ => new RunLedger());
    }

    /// <summary>
    /// The ledger for the ROOT of the current run's parent chain (P2-8, <see cref="AgentRunScope.Root"/>) — the
    /// single ledger every nested sub-agent run resolves to, so a budget gate keyed here accumulates across the
    /// whole run tree instead of resetting per nested run. For a top-level (un-nested) run the root IS the current
    /// scope, so this is identical to <see cref="ForCurrentRun"/>; with no run scope at all, both fall back to the
    /// one shared process-wide ledger.
    /// </summary>
    public static RunLedger ForRootRun()
    {
        var key = (object?)AgentRunScope.Current?.Root ?? FallbackKey;
        return PerRun.GetValue(key, static _ => new RunLedger());
    }

    /// <summary>Total tool calls recorded across every tool this run.</summary>
    public int TotalToolCalls
    {
        get { lock (_lock) { return _totalToolCalls; } }
    }

    /// <summary>
    /// Snapshots this run's accumulated tool-call activity into a <see cref="RunReceipt"/> (P3-11) — an atomic
    /// copy under the ledger lock, so it is consistent even if a concurrent tool call is in flight.
    /// </summary>
    public RunReceipt Summarize(string? runId, string? agentName, string? configFingerprint, DateTimeOffset endedAtUtc)
    {
        lock (_lock)
        {
            return new RunReceipt(runId, agentName, configFingerprint, endedAtUtc, _totalToolCalls,
                new Dictionary<string, int>(_toolCalls, StringComparer.OrdinalIgnoreCase));
        }
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

    /// <summary>
    /// Whether an id was legitimately surfaced by an earlier tool this run. Not consumed by a shipped gate today
    /// (<c>ReferentialIntegrityGate</c> recomputes statelessly from the run history); available for a custom check.
    /// </summary>
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
    /// is correct even when tool calls are invoked concurrently. A negative <paramref name="monetaryAmount"/> is
    /// defensively clamped to zero here (the caller also clamps) so it can never reduce the running sum and
    /// manufacture budget headroom.
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

    /// <summary>The running sum <see cref="MonetaryLimitGate"/> has admitted for <paramref name="argName"/> this run.</summary>
    public decimal MonetaryLimitSum(string argName)
    {
        ArgumentNullException.ThrowIfNull(argName);
        lock (_lock)
        {
            return _monetaryLimitSums.TryGetValue(argName, out var v) ? v : 0m;
        }
    }

    /// <summary>How many times <see cref="PerToolCallBudgetGate"/> has admitted a call to <paramref name="toolName"/> this run.</summary>
    public int PerToolCallBudgetCount(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        lock (_lock)
        {
            return _perToolCallBudgetCounts.TryGetValue(toolName, out var n) ? n : 0;
        }
    }

    /// <summary>
    /// Atomically checks and records <b>only</b> the <see cref="MonetaryLimitGate"/> dimension for
    /// <paramref name="argName"/> — isolated from <see cref="TryAdmitToolCall"/>'s monetary bookkeeping (see the
    /// class doc). A negative <paramref name="amount"/> is defensively clamped to zero so it can never reduce the
    /// running sum and manufacture headroom under the cap.
    /// </summary>
    public RunBudgetDecision TryAdmitMonetary(string argName, decimal amount, decimal max)
    {
        ArgumentNullException.ThrowIfNull(argName);
        if (amount < 0m)
        {
            amount = 0m;
        }

        lock (_lock)
        {
            var cur = _monetaryLimitSums.TryGetValue(argName, out var v) ? v : 0m;
            if (cur + amount > max)
            {
                return RunBudgetDecision.MonetaryExceeded;
            }

            _monetaryLimitSums[argName] = cur + amount;
            return RunBudgetDecision.Admitted;
        }
    }

    /// <summary>
    /// Atomically checks and records <b>only</b> the <see cref="PerToolCallBudgetGate"/> dimension for
    /// <paramref name="toolName"/> — isolated from <see cref="TryAdmitToolCall"/>'s per-tool bookkeeping (see the
    /// class doc), so it never sees a count inflated by a co-registered <see cref="RunBudgetGate"/> (or vice versa).
    /// </summary>
    public RunBudgetDecision TryAdmitPerToolCall(string toolName, int maxPerTool)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        lock (_lock)
        {
            var cur = _perToolCallBudgetCounts.TryGetValue(toolName, out var n) ? n : 0;
            if (cur >= maxPerTool)
            {
                return RunBudgetDecision.PerToolExceeded;
            }

            _perToolCallBudgetCounts[toolName] = cur + 1;
            return RunBudgetDecision.Admitted;
        }
    }

    /// <summary>
    /// Atomically records one tool call's result SIZE (character count) this run and returns the running
    /// snapshot AS IT WAS <b>BEFORE</b> this call — the baseline <see cref="ToolResultSizeAnomalyGate"/>
    /// compares this call's size against, not including it (so the very first call for a tool always sees
    /// <see cref="ToolResultSizeSnapshot.Count"/> 0 — no baseline yet). Isolated storage (own dictionary, not
    /// shared with any other dimension's bookkeeping) — same no-cross-contamination discipline as every other
    /// RunLedger dimension (see class doc). A negative <paramref name="size"/> is defensively clamped to zero.
    /// </summary>
    public ToolResultSizeSnapshot RecordToolResultSize(string toolName, long size)
        => RecordToolResultSizeUnlessAnomalous(toolName, size, static _ => false);

    /// <summary>
    /// Same atomic read-baseline-then-record contract as <see cref="RecordToolResultSize"/>, except the size
    /// is folded into the tool's running baseline ONLY when <paramref name="isAnomalous"/> — evaluated against
    /// the baseline as it stood before this call, inside the same lock — returns <see langword="false"/>.
    /// Exists so <see cref="ToolResultSizeAnomalyGate"/> never lets a result it just flagged as anomalous
    /// permanently inflate that tool's own future baseline: without this, a repeated attack of the same size
    /// would evade detection the second time, because the first (correctly flagged) attack already dragged the
    /// average up past the threshold that would have caught the second one. <paramref name="isAnomalous"/> must
    /// be a cheap, non-blocking, side-effect-free predicate — it runs inside the lock.
    /// </summary>
    public ToolResultSizeSnapshot RecordToolResultSizeUnlessAnomalous(string toolName, long size, Func<ToolResultSizeSnapshot, bool> isAnomalous)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(isAnomalous);
        if (size < 0)
        {
            size = 0;
        }

        lock (_lock)
        {
            var before = _toolResultSizes.TryGetValue(toolName, out var stats) ? stats : (Count: 0, Sum: 0L, Max: 0L);
            var baseline = new ToolResultSizeSnapshot(before.Count, before.Sum, before.Max);
            if (!isAnomalous(baseline))
            {
                _toolResultSizes[toolName] = (before.Count + 1, before.Sum + size, Math.Max(before.Max, size));
            }

            return baseline;
        }
    }

    /// <summary>
    /// Records one denial for <paramref name="denialKey"/> (a tool + arg-shape signature) and returns the running
    /// count INCLUDING this one (Phase 4, P4-3 / F-B). An equivalent retry — same tool, same argument shape —
    /// shares the key, so the returned count is "how many times this same denied action has been tried". Surfaced
    /// as the refusal envelope's <c>attempts</c>; also the dimension the Bulkhead RepeatedBlockEscalationGate reads.
    /// </summary>
    public int RecordDenial(string denialKey)
    {
        ArgumentNullException.ThrowIfNull(denialKey);
        lock (_lock)
        {
            var count = (_denials.TryGetValue(denialKey, out var n) ? n : 0) + 1;
            _denials[denialKey] = count;
            return count;
        }
    }

    /// <summary>The number of denials recorded so far for <paramref name="denialKey"/> this run (0 if none).</summary>
    public int DenialCount(string denialKey)
    {
        ArgumentNullException.ThrowIfNull(denialKey);
        lock (_lock)
        {
            return _denials.TryGetValue(denialKey, out var n) ? n : 0;
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
