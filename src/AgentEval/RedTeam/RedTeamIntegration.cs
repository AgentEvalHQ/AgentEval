// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.RedTeam;

/// <summary>
/// Extension methods for integrating RedTeam with AgentEval.
/// </summary>
public static class RedTeamIntegration
{
    /// <summary>
    /// Adds RedTeam services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedTeam(this IServiceCollection services)
    {
        services.AddSingleton<IRedTeamRunner, RedTeamRunner>();
        return services;
    }

    /// <summary>
    /// Runs a quick red team scan with all attacks at Quick intensity.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Red team scan results.</returns>
    public static Task<RedTeamResult> QuickRedTeamScanAsync(
        this IEvaluableAgent agent,
        CancellationToken cancellationToken = default)
    {
        return AttackPipeline
            .Create()
            .WithAllAttacks()
            .WithIntensity(Intensity.Quick)
            .WithTimeout(TimeSpan.FromMinutes(5))
            .ScanAsync(agent, cancellationToken);
    }

    /// <summary>
    /// Runs a moderate red team scan with all attacks at Moderate intensity.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Red team scan results.</returns>
    public static Task<RedTeamResult> ModerateRedTeamScanAsync(
        this IEvaluableAgent agent,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pipeline = AttackPipeline
            .Create()
            .WithAllAttacks()
            .WithIntensity(Intensity.Moderate)
            .WithTimeout(TimeSpan.FromMinutes(15));

        if (progress != null)
        {
            pipeline.WithProgress(progress);
        }

        return pipeline.ScanAsync(agent, cancellationToken);
    }

    /// <summary>
    /// Runs a comprehensive red team scan with all attacks at Comprehensive intensity.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Red team scan results.</returns>
    public static Task<RedTeamResult> ComprehensiveRedTeamScanAsync(
        this IEvaluableAgent agent,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pipeline = AttackPipeline
            .Create()
            .WithAllAttacks()
            .WithIntensity(Intensity.Comprehensive)
            .WithTimeout(TimeSpan.FromMinutes(30));

        if (progress != null)
        {
            pipeline.WithProgress(progress);
        }

        return pipeline.ScanAsync(agent, cancellationToken);
    }

    /// <summary>
    /// Runs specific attacks against the agent.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="attacks">Attack types to run.</param>
    /// <returns>Red team scan results.</returns>
    public static Task<RedTeamResult> RedTeamAsync(
        this IEvaluableAgent agent,
        params IAttackType[] attacks)
    {
        return AttackPipeline
            .Create()
            .WithAttacks(attacks)
            .WithIntensity(Intensity.Moderate)
            .WithTimeout(TimeSpan.FromMinutes(10))
            .ScanAsync(agent);
    }

    /// <summary>
    /// Runs specific attacks against the agent with options.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="options">Scan options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Red team scan results.</returns>
    public static Task<RedTeamResult> RedTeamAsync(
        this IEvaluableAgent agent,
        ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        var runner = new RedTeamRunner();
        return runner.ScanAsync(agent, options, cancellationToken);
    }

    /// <summary>
    /// Tests if the agent can resist a specific attack type.
    /// </summary>
    /// <param name="agent">The agent to test.</param>
    /// <param name="attack">Attack type to test.</param>
    /// <param name="intensity">Scan intensity.</param>
    /// <returns>True if agent resisted all probes; false otherwise.</returns>
    public static async Task<bool> CanResistAsync(
        this IEvaluableAgent agent,
        IAttackType attack,
        Intensity intensity = Intensity.Quick)
    {
        var result = await AttackPipeline
            .Create()
            .WithAttack(attack)
            .WithIntensity(intensity)
            .WithTimeout(TimeSpan.FromMinutes(5))
            .ScanAsync(agent);

        return result.Passed;
    }
}
