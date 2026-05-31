// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Tracing;

/// <summary>
/// Ambient, per-invocation correlation id (Glass Box). Establish it with a using-block around an agent
/// invocation: <c>using var _ = new ToolCorrelationScope(invocationId);</c>. Both
/// <see cref="TraceRecordingChatClient"/> (Core) and <c>EvaluatingAIFunction</c> (MAF) <b>read</b>
/// <see cref="Current"/> to stamp every entry of the invocation with the same <c>CorrelationId</c>;
/// neither sets it. Flows through async/await (AsyncLocal); see the proposal §14 Q2 parallel-tools caveat.
/// </summary>
/// <remarks>
/// Lives in <c>AgentEval.Core</c> (not <c>AgentEval.MAF</c>) on purpose: <c>AgentEval.MAF</c> references
/// <c>AgentEval.Core</c> but not vice-versa, and the Core-resident <see cref="TraceRecordingChatClient"/>
/// must read this scope — so it must be defined here for Phase 1 to compile.
/// </remarks>
public sealed class ToolCorrelationScope : IDisposable
{
    private static readonly AsyncLocal<string?> CurrentScope = new();
    private readonly string? _previous;

    /// <summary>The active correlation id for the current async flow, or null when none is set.</summary>
    public static string? Current => CurrentScope.Value;

    /// <summary>Pushes <paramref name="correlationId"/> as the ambient correlation id (restored on dispose; supports nesting).</summary>
    public ToolCorrelationScope(string correlationId)
    {
        _previous = CurrentScope.Value;
        CurrentScope.Value = correlationId;
    }

    /// <summary>Restores the previously-set correlation id.</summary>
    public void Dispose() => CurrentScope.Value = _previous;
}
