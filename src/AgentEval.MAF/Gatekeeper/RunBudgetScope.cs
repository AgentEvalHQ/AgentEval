// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Which run's <see cref="RunLedger"/> a budget gate (<see cref="RunBudgetGate"/>, <see cref="MonetaryLimitGate"/>,
/// <see cref="PerToolCallBudgetGate"/>) charges against (Phase 2, P2-8).
/// </summary>
public enum RunBudgetScope
{
    /// <summary>
    /// Charge the innermost (current) run's ledger — each nested sub-agent run keeps its OWN budget. The default,
    /// for back-compatibility and for the deliberate "cap each sub-agent independently" pattern. <b>Caveat:</b>
    /// an orchestration that fans out to sub-agent runs can launder past a parent cap under this scope, because
    /// every nested run starts a fresh ledger — choose <see cref="Root"/> to close that hole.
    /// </summary>
    Current,

    /// <summary>
    /// Charge the ROOT run's ledger (<see cref="AgentRunScope.Root"/>) so every nested sub-agent run in the tree
    /// shares ONE budget — a denial-of-wallet defense that cannot be bypassed by fanning out into sub-agents.
    /// For a top-level (un-nested) run this behaves identically to <see cref="Current"/>.
    /// </summary>
    Root,
}
