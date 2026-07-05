// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that blocks a dangerous <em>ordered</em> tool combination: once any of the
/// <c>triggerTools</c> has been called, any subsequent call to a <c>guardedTool</c> is blocked
/// (e.g. <c>read_secrets</c> → <c>send_email</c>).
/// <para><b>Per-run scoped.</b> Sequence state is keyed by the current <see cref="AgentRunScope"/> (established
/// by the run gate), so each run starts fresh and state never leaks across runs. When no run scope is present
/// (the run gate was not registered), state falls back to a single shared set — a fresh gate instance per run
/// is then the caller's responsibility.</para>
/// </summary>
public sealed class SequenceGate : IToolGate
{
    private readonly HashSet<string> _triggers;
    private readonly HashSet<string> _guarded;

    // Per-run seen-trigger sets, keyed by the run scope (weak, so they are collected when the run ends). The
    // fallback key gives a single shared set when there is no run scope.
    private readonly ConditionalWeakTable<object, HashSet<string>> _perRun = new();
    private readonly object _fallbackKey = new();

    /// <inheritdoc/>
    public string PolicyName => "SequenceGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate: a call to any <paramref name="guardedTools"/> after any <paramref name="triggerTools"/> is blocked.</summary>
    public SequenceGate(IEnumerable<string> triggerTools, IEnumerable<string> guardedTools)
    {
        ArgumentNullException.ThrowIfNull(triggerTools);
        ArgumentNullException.ThrowIfNull(guardedTools);
        _triggers = new HashSet<string>(triggerTools, StringComparer.OrdinalIgnoreCase);
        _guarded = new HashSet<string>(guardedTools, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var key = (object?)AgentRunScope.Current ?? _fallbackKey;
        var seen = _perRun.GetValue(key, static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        bool block;
        string seenList;
        lock (seen)   // serialize check + mutate + reason-build for THIS run's set (fixes the enumeration race)
        {
            block = _guarded.Contains(call.FunctionName) && seen.Count > 0;
            if (_triggers.Contains(call.FunctionName))
            {
                seen.Add(call.FunctionName);
            }

            seenList = block ? string.Join(", ", seen) : string.Empty;
        }

        return new ValueTask<ToolGateVerdict>(block
            ? ToolGateVerdict.Block(PolicyName, $"tool '{call.FunctionName}' is blocked after a trigger tool ({seenList}) was used")
            : ToolGateVerdict.Allow(PolicyName));
    }
}
