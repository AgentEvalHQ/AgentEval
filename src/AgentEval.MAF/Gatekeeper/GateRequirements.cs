// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-6) — a capability flag a stateful <see cref="IToolGate"/> declares about what it
/// needs from its environment to enforce correctly. Today this has exactly one flag (<see cref="RunScope"/>),
/// covering the one documented failure mode the Gatekeeper hardening review calls out: <see cref="RunLedger"/>-
/// backed gates (<see cref="RunBudgetGate"/>, <see cref="MonetaryLimitGate"/>, <see cref="PerToolCallBudgetGate"/>)
/// and <see cref="SequenceGate"/> silently fall back to ONE process-wide shared state object when no
/// <see cref="AgentRunScope"/> is established — self-documented on each of those classes, never logged or
/// thrown. <c>UseGatekeeper</c> uses this flag to fail construction loudly instead, in enforcement mode, when a
/// gate that needs it is registered without run-scope guaranteed.
/// </summary>
[Flags]
public enum GateRequirements
{
    /// <summary>No special requirement — safe to run under any composition, with or without a run scope.</summary>
    None = 0,

    /// <summary>
    /// This gate's correctness depends on an <see cref="AgentRunScope"/> being established (via
    /// <c>UseAgentEvalGate</c>, or the composite <c>UseGatekeeper</c>) for the current run. Without one, the
    /// gate still runs — but its state (a budget, a sequence-trigger set, a monetary sum) silently falls back
    /// to a single process-wide instance shared by every run that also lacks a scope, defeating per-run
    /// isolation. Advisory-only (WarnOnly / observe) callers may still accept this fallback — the stakes are
    /// lower there; enforcement-mode callers should not.
    /// </summary>
    RunScope = 1 << 0,
}
