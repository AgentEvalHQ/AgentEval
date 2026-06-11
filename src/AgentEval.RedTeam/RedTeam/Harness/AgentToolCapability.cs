// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Opt-in capabilities a <see cref="IToolCapableAgent"/> advertises. Declared by the agent; the
/// <see cref="TierNegotiator"/> still classifies the actual <see cref="SutTier"/> from these flags so the harness
/// never over-claims (floor, never ceiling).
/// </summary>
[Flags]
public enum AgentToolCapability
{
    /// <summary>No tool capability — runs at <see cref="SutTier.TextOnly"/>.</summary>
    None = 0,

    /// <summary>Can accept canary tool schemas and emit <c>tool_call</c>s (Tier 1; emitted, not executed).</summary>
    FunctionCalling = 1 << 0,

    /// <summary>Canary tools actually run and record execution (Tier 2; act + effect).</summary>
    InstrumentedTools = 1 << 1,

    /// <summary>Can carry tool results across multiple turns (reserved for Wave C multi-turn).</summary>
    MultiTurnTools = 1 << 2,

    /// <summary>
    /// Advertises that the agent supports Pillar-4 tool-output injection — an attacker payload delivered through a
    /// tool result (e.g. <see cref="InstrumentedCanaryAgent"/>, whose <see cref="CanaryTool.Execute"/> can return
    /// attacker-controlled output). A declarative capability marker for callers/reporting; the runner does not branch
    /// on it today (the IndirectInjection tool-output path is driven by the canary's own <c>Execute</c> body).
    /// </summary>
    ToolOutputInjection = 1 << 3,
}
