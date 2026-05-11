// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Memory.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Extension methods for registering all AgentEval services including RedTeam and Memory.
/// This extension is provided by the umbrella package. Use
/// <see cref="AgentEvalServiceCollectionExtensions.AddAgentEval"/> (Core),
/// <see cref="DataLoaderServiceCollectionExtensions.AddAgentEvalDataLoaders"/> (DataLoaders),
/// <see cref="RedTeamServiceCollectionExtensions.AddAgentEvalRedTeam"/> (RedTeam), and
/// <see cref="AgentEvalMemoryServiceCollectionExtensions.AddAgentEvalMemory"/> (Memory)
/// individually if you only need a subset.
/// </summary>
public static class AgentEvalFullServiceCollectionExtensions
{
    /// <summary>
    /// Adds all AgentEval services (Core + DataLoaders + RedTeam + Memory) to the service collection.
    /// This is the recommended entry point for consumers of the full AgentEval NuGet package.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Optional action to configure AgentEval options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalAll(
        this IServiceCollection services,
        Action<AgentEvalServiceOptions>? configure = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Register Core services (metrics, comparison, calibration, snapshots, embeddings, etc.)
        services.AddAgentEval(configure);

        // Register DataLoader and Exporter services
        services.AddAgentEvalDataLoaders();

        // Register RedTeam services
        services.AddAgentEvalRedTeam();

        // Register Memory evaluation services (memory benchmark runner, judge, scenarios, metrics, reporting).
        services.AddAgentEvalMemory();

        return services;
    }
}
