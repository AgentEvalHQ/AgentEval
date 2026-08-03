// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Explicit dependency-injection registration for composite memory protection.</summary>
public static class MemoryProtectionServiceCollectionExtensions
{
    /// <summary>Registers one caller-built memory-protection configuration as a singleton.</summary>
    public static IServiceCollection AddAgentEvalMemoryProtection(
        this IServiceCollection services,
        MemoryProtectionOptions protection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(protection);
        services.AddSingleton(protection);
        return services;
    }

    /// <summary>Registers a singleton factory that resolves the complete runtime configuration.</summary>
    public static IServiceCollection AddAgentEvalMemoryProtection(
        this IServiceCollection services,
        Func<IServiceProvider, MemoryProtectionOptions> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(provider =>
            factory(provider) ?? throw new InvalidOperationException(
                "The AgentEval memory-protection factory returned null."));
        return services;
    }
}
