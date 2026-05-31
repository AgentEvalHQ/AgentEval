// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Ambient context that hands the agent-boundary + chat-boundary trace pair to the Shape-B
/// <c>RunnerFactory</c> (the registry-driven path). Populate it with a using-block before invoking the
/// factory: <c>using var ctx = new TraceFidelityRunnerHostingContext(agentTrace, chatTrace);</c>.
/// </summary>
/// <remarks>
/// Modelled on <c>LongMemEvalRunnerHostingContext</c>. The CLI handler constructs the runner directly
/// (it already holds both traces); this context exists for the generic registry-driven / test path.
/// </remarks>
public sealed class TraceFidelityRunnerHostingContext : IDisposable
{
    private static readonly AsyncLocal<TraceFidelityRunnerHostingContext?> CurrentContext = new();
    private readonly TraceFidelityRunnerHostingContext? _previous;

    /// <summary>The active context for the current async flow, or null when none is set.</summary>
    public static TraceFidelityRunnerHostingContext? Current => CurrentContext.Value;

    /// <summary>The agent-boundary trace (the framework's self-report).</summary>
    public AgentTrace AgentBoundaryTrace { get; }

    /// <summary>The chat-boundary trace (ground truth at the model interface).</summary>
    public AgentTrace ChatBoundaryTrace { get; }

    /// <summary>Pushes a context carrying the trace pair onto the AsyncLocal stack (restored on dispose).</summary>
    public TraceFidelityRunnerHostingContext(AgentTrace agentBoundaryTrace, AgentTrace chatBoundaryTrace)
    {
        AgentBoundaryTrace = agentBoundaryTrace ?? throw new ArgumentNullException(nameof(agentBoundaryTrace));
        ChatBoundaryTrace = chatBoundaryTrace ?? throw new ArgumentNullException(nameof(chatBoundaryTrace));
        _previous = CurrentContext.Value;
        CurrentContext.Value = this;
    }

    /// <summary>Restores the previously-set context (supports nesting).</summary>
    public void Dispose() => CurrentContext.Value = _previous;
}
