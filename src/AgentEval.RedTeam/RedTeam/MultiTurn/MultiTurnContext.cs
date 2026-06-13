// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // AgentResponse
using AgentEval.Testing;    // Turn
using Microsoft.Extensions.AI;   // IChatClient

namespace AgentEval.RedTeam;

/// <summary>
/// State handed to an <see cref="IMultiTurnAttack"/> to compute the next user turn (Wave C). A <c>record</c> so the
/// orchestrator can <c>with</c>-update it cheaply between the pre- and post-response views.
/// </summary>
public sealed record MultiTurnContext
{
    /// <summary>The seed probe (the objective being escalated toward).</summary>
    public required AttackProbe Seed { get; init; }

    /// <summary>Turns exchanged so far (oldest → newest).</summary>
    public required IReadOnlyList<Turn> History { get; init; }

    /// <summary>0-based index of the turn being produced.</summary>
    public int TurnIndex { get; init; }

    /// <summary>The agent's previous reply (<c>null</c> on turn 0).</summary>
    public AgentResponse? LastResponse { get; init; }

    /// <summary>The fidelity of the conversation channel in use.</summary>
    public ConversationFidelity Fidelity { get; init; }

    /// <summary>
    /// Optional <b>attacker-LLM</b> client for LLM-driven rung generation (Wave C′; <c>null</c> ⇒ scripted ladder).
    /// Distinct from <see cref="ScanOptions.JudgeClient"/> (the <em>verdict</em> judge used by the orchestrator to
    /// resolve Inconclusive turns, GAP-19) — an attack generates turns, a judge scores them; they must not be conflated.
    /// </summary>
    public IChatClient? AttackerClient { get; init; }
}
