// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// What a tool gate <em>found</em> about a call. How a <see cref="Block"/> is <em>enforced</em> (warn / replace
/// / throw / terminate) is decided by the <see cref="ToolGatePolicy"/>; <see cref="Mutate"/> is applied
/// regardless of policy. Self-contained (not the chat <c>GateAction</c>, which only has Allow/Block) — but the
/// <c>Block</c> token stringifies to <c>"Block"</c>, exactly what the shipped Glass Box evidence reader counts.
/// </summary>
public enum ToolGateAction
{
    /// <summary>The call is acceptable.</summary>
    Allow,

    /// <summary>The call violates the policy; enforced per <see cref="ToolGatePolicy"/>.</summary>
    Block,

    /// <summary>The gate rewrites the call's arguments and lets the (mutated) call proceed.</summary>
    Mutate,
}
