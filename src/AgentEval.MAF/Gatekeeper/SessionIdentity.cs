// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Ready-made resolvers for a <b>durable logical session id</b> (F-A, Fable 5 P1-4) — the id per-session gates
/// (including <see cref="RateLimitGate"/> and <see cref="SessionIdentityDriftGate"/>) key on so state survives
/// a persisted-session reload or a move between in-process workers, instead of resetting with each
/// fresh <see cref="AgentSession"/>
/// object. Set one on <see cref="GatekeeperOptions.SessionIdentity"/> and <c>UseGatekeeper</c> injects it into every
/// <see cref="ISessionIdentityAware"/> gate. A resolver returning <see langword="null"/>/empty for a session means
/// "no durable id here" — the gate falls back to object identity for that session, so a partial rollout degrades
/// gracefully rather than throwing.
/// </summary>
public static class SessionIdentity
{
    /// <summary>
    /// Resolves the id from the session's <c>StateBag</c> under <paramref name="key"/> — where a host stashes the
    /// durable id it already knows. Returns <see langword="null"/> when the key is absent or not a string.
    /// <para>⚠️ <b>Use a server-attested id only.</b> When this feeds a <i>rate limit</i> or budget, the id source
    /// must be something the client cannot freely choose — an auth-claim subject, a server-issued session key, a
    /// tenant id — NOT a client-supplied header/correlation id. A client that can rotate the id mints a fresh
    /// zero counter per request and defeats the cap (and a high-cardinality rotation grows the gate's per-id table
    /// unboundedly). Keying on a rotatable client value is no better than the object-identity default it replaces.</para>
    /// </summary>
    public static Func<AgentSession, string?> FromStateBag(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("key must be non-empty.", nameof(key));
        }

        return session =>
        {
            ArgumentNullException.ThrowIfNull(session);
            return session.StateBag.TryGetValue<string>(key, out var id, JsonSerializerOptions.Default) ? id : null;
        };
    }

    /// <summary>
    /// Chains resolvers: returns the first non-empty id. Use it to prefer a strong id (e.g. an auth claim) and fall
    /// back to a weaker durable one before finally degrading to object identity — <c>Combine(FromStateBag("uid"),
    /// FromStateBag("conversationId"))</c>.
    /// </summary>
    public static Func<AgentSession, string?> Combine(params Func<AgentSession, string?>[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        if (resolvers.Length == 0 || Array.Exists(resolvers, r => r is null))
        {
            throw new ArgumentException("At least one resolver is required, and none may be null.", nameof(resolvers));
        }

        return session =>
        {
            foreach (var resolver in resolvers)
            {
                var id = resolver(session);
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }
            }

            return null;
        };
    }
}
