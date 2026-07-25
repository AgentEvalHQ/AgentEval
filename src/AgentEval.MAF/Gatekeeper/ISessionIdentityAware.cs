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
    /// explicit one of its own</b> (an explicit per-gate resolver always wins). Idempotent and <b>first-write-wins</b>:
    /// once a resolver is set — by an explicit constructor selector or an earlier call — every later call is a no-op.
    /// <para>⚠️ <b>One gate instance per configuration.</b> Because the resolver is stored on the gate, a SINGLE gate
    /// instance shared across two <c>UseGatekeeper</c> configurations that set <i>different</i>
    /// <see cref="GatekeeperOptions.SessionIdentity"/> resolvers keeps only the FIRST — the second is silently
    /// dropped, so that configuration's gate would fall back to object identity (a per-session cap that resets on
    /// reload). Construct a separate gate instance per agent/configuration (the normal pattern), or pass each its own
    /// explicit constructor selector, when their session identities differ.</para>
    /// </summary>
    void UseSessionIdentityDefault(Func<AgentSession, string?> resolver);
}
