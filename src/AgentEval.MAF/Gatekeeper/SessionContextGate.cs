// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Base for run-level gates that need the session context (auth / budget / rate). Reads
/// <see cref="AgentRunScope.Current"/>; if there is no scope or no session, it <b>fails CLOSED</b> (returns
/// Block) — a security gate that cannot read its context must deny, never null-tolerantly allow (SEC-02).
/// Register these as <c>pre</c> gates on <c>UseAgentEvalGate</c> (the run gate establishes the scope first).
/// </summary>
public abstract class SessionContextGate : IChatGate
{
    /// <inheritdoc/>
    public abstract string PolicyName { get; }

    /// <inheritdoc/>
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        var session = AgentRunScope.Current?.Session;
        if (session is null)
        {
            return new ValueTask<GateVerdict>(
                GateVerdict.Block(PolicyName, "no run scope / session available in context — failing closed"));
        }

        return CheckSessionAsync(session, cancellationToken);
    }

    /// <summary>Evaluate the gate against the run's session. Called only when a session is present.</summary>
    protected abstract ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken);
}
