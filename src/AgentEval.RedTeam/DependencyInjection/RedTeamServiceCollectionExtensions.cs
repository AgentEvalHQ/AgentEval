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
    /// Adds AgentEval Red Team security testing services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalRedTeam(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Ensure core services are registered (idempotent via TryAdd*)
        services.AddAgentEval();

        services.TryAddSingleton<IAttackTypeRegistry>(sp =>
            new AttackTypeRegistry(sp.GetServices<IAttackType>()));

        return services;
    }
}
