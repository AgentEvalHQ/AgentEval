// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Opt-in seam (Phase 2, P2-3) for a session that can <b>scrub its own memory</b> after an enforced run-post
/// Block/Redact. When a run-post gate withholds or replaces a response, the model's ORIGINAL (unsafe) turn has
/// already been appended to the <see cref="AgentSession"/> and would re-enter the model's context on the next
/// turn — the caller sees a refusal, but the agent still "remembers" the blocked content.
/// <para><b>Why a seam, not a built-in scrub.</b> MAF's built-in <c>ChatClientAgentSession</c> does not expose a
/// mutable message history through any stable public API (it carries only a conversation id and an opaque state
/// bag), so the Gatekeeper cannot rewrite its persisted turn without reflecting into version-specific internals —
/// which this repo deliberately avoids (MAF ships breaking changes every minor version). Instead, a session that
/// owns a reachable history <b>implements this interface</b> (directly, or exposes it via
/// <see cref="AgentSession.GetService(System.Type, object?)"/>) to receive the reconciliation call. Sessions that
/// don't are left untouched, and the divergence is still recorded as honest, auditable evidence (never a silent
/// or falsely-successful scrub) — see <c>gate.session.*</c> reconciliation records.</para>
/// </summary>
public interface IReconcilableSession
{
    /// <summary>
    /// Rewrite the most recent persisted assistant turn to <paramref name="safeText"/> — the exact text the caller
    /// saw after an enforced run-post Block/Redact — so the withheld unsafe response does not re-enter context on
    /// the next turn. Returns <see langword="true"/> if a turn was actually rewritten (so the reconciler records a
    /// truthful outcome), <see langword="false"/> if there was nothing to reconcile.
    /// </summary>
    bool TryReconcileLastAssistantMessage(string safeText);
}
