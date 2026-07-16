// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, #18) — lightweight, in-process gate-effectiveness telemetry: which tool gates fired,
/// how often, with what verdict, and how much latency they added. A thin counter/histogram surface bolted onto
/// the existing per-call gate loop in <c>UseAgentEvalToolGate</c> — NOT a new subsystem, no I/O, no new
/// dependency. Caller-owned (create one, pass it to <c>UseAgentEvalToolGate</c>/<c>UseGatekeeper</c>, read it
/// whenever you like — the same ownership shape as <c>ShadowJudgePump</c>).
/// <para>Pairs with <see cref="GatekeeperCoverageAnalyzer"/>: the coverage report answers "is this tool
/// structurally reachable by a gate," this answers "when it ran, what did the gate actually do."</para>
/// <para><b>Thread-safe.</b> Tool calls can be invoked concurrently (<c>AllowConcurrentInvocation</c>); every
/// counter here is either an <see cref="Interlocked"/> primitive or a <see cref="ConcurrentDictionary{TKey,TValue}"/> entry.</para>
/// </summary>
public sealed class GateTelemetry
{
    private readonly ConcurrentDictionary<string, Cell> _cells = new(StringComparer.Ordinal);

    /// <summary>Records one gate invocation's outcome and elapsed time. Called by the tool-gate loop; not typically called directly.</summary>
    public void Record(string policyName, ToolGateAction action, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        var cell = _cells.GetOrAdd(policyName, static _ => new Cell());
        cell.Record(action, elapsed);
    }

    /// <summary>A snapshot of every gate's counters, as of now. Safe to call at any time, including concurrently with live traffic.</summary>
    public IReadOnlyList<GateTelemetrySnapshot> Snapshot()
        => _cells.Select(kv => kv.Value.ToSnapshot(kv.Key)).OrderBy(s => s.PolicyName, StringComparer.Ordinal).ToArray();

    /// <summary>Resets every counter. Intended for test isolation / long-lived-process periodic rollover.</summary>
    public void Reset() => _cells.Clear();

    private sealed class Cell
    {
        private long _invocations;
        private long _allowed;
        private long _blocked;
        private long _mutated;
        private long _totalElapsedTicks;

        public void Record(ToolGateAction action, TimeSpan elapsed)
        {
            Interlocked.Increment(ref _invocations);
            Interlocked.Add(ref _totalElapsedTicks, elapsed.Ticks);
            switch (action)
            {
                case ToolGateAction.Block:
                    Interlocked.Increment(ref _blocked);
                    break;
                case ToolGateAction.Mutate:
                    Interlocked.Increment(ref _mutated);
                    break;
                default:
                    Interlocked.Increment(ref _allowed);
                    break;
            }
        }

        // NOTE: this records what the gate FOUND (verdict.Action), not whether the enforcing ToolGatePolicy
        // actually blocked the call — a WarnOnly Block finding still counts as BlockCount here. That mirrors
        // "did the gate fire," which is what effectiveness telemetry is for; cross-reference the trace's
        // gate.tool.* action ("Block" vs "Warn") if you need the enforced/observed split too.
        public GateTelemetrySnapshot ToSnapshot(string policyName)
        {
            var invocations = Interlocked.Read(ref _invocations);
            var totalElapsed = TimeSpan.FromTicks(Interlocked.Read(ref _totalElapsedTicks));
            return new GateTelemetrySnapshot(
                PolicyName: policyName,
                InvocationCount: invocations,
                AllowCount: Interlocked.Read(ref _allowed),
                BlockCount: Interlocked.Read(ref _blocked),
                MutateCount: Interlocked.Read(ref _mutated),
                TotalElapsed: totalElapsed,
                AverageElapsed: invocations == 0 ? TimeSpan.Zero : totalElapsed / invocations);
        }
    }
}

/// <summary>A point-in-time snapshot of one tool gate's effectiveness counters.</summary>
/// <param name="PolicyName">The gate's <see cref="IToolGate.PolicyName"/>.</param>
/// <param name="InvocationCount">Total calls this gate inspected.</param>
/// <param name="AllowCount">Calls this gate allowed.</param>
/// <param name="BlockCount">Calls this gate found and blocked (regardless of <see cref="ToolGatePolicy"/> — a WarnOnly finding still counts here; it answers "did the gate fire," not "was it enforced").</param>
/// <param name="MutateCount">Calls this gate rewrote.</param>
/// <param name="TotalElapsed">Cumulative time spent inside this gate's <see cref="IToolGate.InspectAsync"/>.</param>
/// <param name="AverageElapsed">Mean per-invocation latency this gate added to the tool-invocation hot path.</param>
public sealed record GateTelemetrySnapshot(
    string PolicyName,
    long InvocationCount,
    long AllowCount,
    long BlockCount,
    long MutateCount,
    TimeSpan TotalElapsed,
    TimeSpan AverageElapsed);
