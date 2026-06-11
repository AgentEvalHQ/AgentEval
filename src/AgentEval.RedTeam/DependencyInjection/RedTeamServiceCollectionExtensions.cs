// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Extension methods for registering AgentEval Red Team security testing services.
/// </summary>
public static class RedTeamServiceCollectionExtensions
{
    /// <summary>
    /// Adds AgentEval Red Team security testing services to the service collection and, by default, registers
    /// all built-in <see cref="IAttackType"/>s so a freshly-configured container can scan immediately (N-11).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="attacks">
    /// Optional explicit attack set to register as <see cref="IAttackType"/> services in the container. When
    /// <c>null</c> (default), the full built-in roster (<see cref="Attack.All"/>) is registered. This governs only
    /// the DI <see cref="IAttackType"/> registrations (what <c>GetServices&lt;IAttackType&gt;()</c> returns); the
    /// resolved <see cref="IAttackTypeRegistry"/> is always pre-seeded with the built-in roster regardless, so a
    /// subset here does NOT shrink the registry.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalRedTeam(
        this IServiceCollection services,
        IEnumerable<IAttackType>? attacks = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Ensure core services are registered (idempotent via TryAdd*)
        services.AddAgentEval();

        // N-11: register the attack types as IAttackType services. Without this, GetServices<IAttackType>()
        // resolves EMPTY — a consumer enumerating attack types from the container gets nothing. (The
        // IAttackTypeRegistry is already pre-seeded with Attack.All in its ctor, so scans themselves were
        // unaffected; this makes the raw DI enumerable consistent.) TryAddEnumerable keeps registration
        // idempotent (dedup by implementation type) so calling AddAgentEvalRedTeam twice does not double the roster.
        foreach (var attack in attacks ?? Attack.All)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAttackType>(attack));

        services.TryAddSingleton<IAttackTypeRegistry>(sp =>
            new AttackTypeRegistry(sp.GetServices<IAttackType>()));

        return services;
    }
}
