// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M2) — an ambient scope carrying the current run's context (session, agent name, trace)
/// so an <b>inner</b> tool gate or session gate can read it. Modeled on <c>ToolCorrelationScope</c> but carries
/// MAF types, so it lives in AgentEval.MAF.
/// <para>⚠️ <b>Streaming:</b> an AsyncLocal set once at the top of an async iterator does NOT survive the first
/// <c>yield return</c> (subsequent <c>MoveNextAsync</c> runs on the consumer's ExecutionContext). The run gate's
/// streaming branch therefore re-establishes the scope <b>per segment</b> — see <c>UseAgentEvalGate</c>.</para>
/// </summary>
public sealed class AgentRunScope : IDisposable
{
    private static readonly AsyncLocal<AgentRunScope?> CurrentScope = new();

    /// <summary>The scope currently in effect on this async flow, or null.</summary>
    public static AgentRunScope? Current => CurrentScope.Value;

    /// <summary>The run's session (may be null — a run can be issued without one).</summary>
    public AgentSession? Session { get; }

    /// <summary>The invoking agent's name, if known.</summary>
    public string? AgentName { get; }

    /// <summary>The Glass Box trace for this run, if any.</summary>
    public AgentTrace? Trace { get; }

    /// <summary>
    /// The scope of the ENCLOSING run (P2-8) — the scope that was <see cref="Current"/> when this one began,
    /// which for a sub-agent / nested run IS the parent run's scope; null for a top-level run. Distinct in intent
    /// from the private restore target even though they coincide by construction: budget gates walk this chain
    /// (see <see cref="Root"/>) so a fan-out of nested runs cannot each start a fresh budget and launder past a
    /// parent cap.
    /// </summary>
    public AgentRunScope? Parent => _previous;

    /// <summary>
    /// The OUTERMOST scope in this run's parent chain — this scope itself when it has no <see cref="Parent"/>
    /// (P2-8). A budget gate keyed on the root's ledger accumulates across the whole run tree, so every nested
    /// sub-agent run shares one budget instead of resetting it.
    /// </summary>
    public AgentRunScope Root
    {
        get
        {
            var scope = this;
            while (scope._previous is { } parent)
            {
                scope = parent;
            }

            return scope;
        }
    }

    private readonly AgentRunScope? _previous;
    private bool _disposed;

    private AgentRunScope(AgentSession? session, string? agentName, AgentTrace? trace)
    {
        Session = session;
        AgentName = agentName;
        Trace = trace;
        _previous = CurrentScope.Value;   // nesting-safe restore
        CurrentScope.Value = this;
    }

    /// <summary>Begins a scope for the current run. Dispose (via <c>using</c>) restores the previous scope.</summary>
    public static AgentRunScope Begin(AgentSession? session, string? agentName, AgentTrace? trace)
        => new(session, agentName, trace);

    /// <summary>
    /// Re-assert THIS scope as <see cref="Current"/> on the calling async flow, returning a disposable that
    /// restores the previous scope. Used by the streaming run gate to keep ONE run identity stable across
    /// stream segments — an AsyncLocal set once does not survive a <c>yield</c>, so each segment re-enters the
    /// SAME scope instance (so per-run state keyed on the scope, e.g. <c>SequenceGate</c>, is not fragmented).
    /// </summary>
    public IDisposable Enter() => new Reassertion(this);

    private sealed class Reassertion : IDisposable
    {
        private readonly AgentRunScope? _previous;
        private bool _disposed;

        public Reassertion(AgentRunScope scope)
        {
            _previous = CurrentScope.Value;
            CurrentScope.Value = scope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentScope.Value = _previous;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CurrentScope.Value = _previous;
    }
}
