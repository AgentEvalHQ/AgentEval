// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.Evals;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to register composite-eval primitives.
/// </summary>
public static class CompositeEvalsServiceExtensions
{
    /// <summary>
    /// Registers the composite-evals primitives in the service collection.
    /// Phase 1 only registers <see cref="WeightedSumAggregation"/> as the default
    /// <see cref="IAggregationStrategy"/>. Future phases add additional strategies.
    /// </summary>
    public static IServiceCollection AddCompositeEvals(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAggregationStrategy>(WeightedSumAggregation.Instance);
        return services;
    }
}
