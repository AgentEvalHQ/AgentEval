// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A gate that keys per-session state on a <b>durable logical session id</b> and can adopt a shared resolver for it
/// (F-A, Fable 5 P1-4). <c>UseGatekeeper</c> injects <see cref="GatekeeperOptions.SessionIdentity"/> into every gate
/// implementing this, so a deployment configures "how do I identify a session across reloads / across workers" ONCE
/// instead of per gate. A gate that was already given its own explicit resolver keeps it — the shared one is only a
/// <i>default</i>. Implemented today by <see cref="RateLimitGate"/>; the primitive the future containment track keys
/// <c>ContainmentTarget.Session(id)</c> on.
/// </summary>
public interface ISessionIdentityAware
{
    /// <summary>
    /// Adopt <paramref name="resolver"/> as the session-identity resolver <b>unless this gate already has an
    /// explicit one of its own</b> (an explicit per-gate resolver always wins). Idempotent: calling it again is a
    /// no-op once a resolver is set.
    /// </summary>
    void UseSessionIdentityDefault(Func<AgentSession, string?> resolver);
}
