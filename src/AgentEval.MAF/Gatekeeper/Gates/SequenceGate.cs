// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that blocks a dangerous <em>ordered</em> tool combination: once any of the
/// <c>triggerTools</c> has been called, any subsequent call to a <c>guardedTool</c> is blocked
/// (e.g. <c>read_secrets</c> → <c>send_email</c>).
/// <para>⚠️ <b>Single-run scope.</b> Sequence state lives on the gate instance, so construct a <b>fresh
/// SequenceGate per agent/run</b>. Cross-run reset (a persistent per-session ordered history) requires the
/// run-level scope that lands with the run gate (M2); until then, reusing one instance across runs accumulates.</para>
/// </summary>
public sealed class SequenceGate : IToolGate
{
    private readonly HashSet<string> _triggers;
    private readonly HashSet<string> _guarded;
    private readonly HashSet<string> _seenTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

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
        bool block;
        lock (_lock)
        {
            // Block a guarded tool ONLY if a trigger was already seen earlier in this run.
            block = _guarded.Contains(call.FunctionName) && _seenTriggers.Count > 0;
            if (_triggers.Contains(call.FunctionName))
            {
                _seenTriggers.Add(call.FunctionName);
            }
        }

        return new ValueTask<ToolGateVerdict>(block
            ? ToolGateVerdict.Block(PolicyName, $"tool '{call.FunctionName}' is blocked after a trigger tool ({string.Join(", ", _seenTriggers)}) was used")
            : ToolGateVerdict.Allow(PolicyName));
    }
}
