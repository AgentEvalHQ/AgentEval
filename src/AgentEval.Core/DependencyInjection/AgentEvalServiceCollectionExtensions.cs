// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Embeddings;
using AgentEval.Snapshots;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Extension methods for configuring AgentEval core services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class AgentEvalServiceCollectionExtensions
{
    /// <summary>
    /// Adds AgentEval core services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Optional action to configure AgentEval options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEval(
        this IServiceCollection services,
        Action<AgentEvalServiceOptions>? configure = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        var options = new AgentEvalServiceOptions();
        configure?.Invoke(options);

        // Register core utilities as singletons (stateless)
        services.TryAddSingleton<IStatisticsCalculator, DefaultStatisticsCalculator>();
        services.TryAddSingleton<IToolUsageExtractor, DefaultToolUsageExtractor>();

        // Register snapshot services as singletons (stateless comparers)
        services.TryAddSingleton<ISnapshotComparer, SnapshotComparer>();

        // Register IMetricRegistry: auto-populates from DI-registered IMetric services
        services.TryAddSingleton<IMetricRegistry>(sp =>
        {
            var registry = new MetricRegistry();
            foreach (var metric in sp.GetServices<IMetric>())
            {
                if (!registry.Contains(metric.Name))
                {
                    registry.Register(metric);
                }
            }
            return registry;
        });

        // Register IAgentEvalEmbeddings: wraps IEmbeddingGenerator if available (G4)
        services.TryAddSingleton<IAgentEvalEmbeddings>(sp =>
            new MEAIEmbeddingAdapter(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        // Register IEvaluator: default ChatClientEvaluator using IChatClient (G9)
        services.TryAddSingleton<IEvaluator>(sp =>
            new ChatClientEvaluator(sp.GetRequiredService<IChatClient>()));

        // Register runners and comparers with appropriate lifetimes
        switch (options.ServiceLifetime)
        {
            case ServiceLifetime.Singleton:
                services.TryAddSingleton<IStochasticRunner, StochasticRunner>();
                services.TryAddSingleton<IModelComparer>(sp => 
                    new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
                break;
            case ServiceLifetime.Scoped:
                services.TryAddScoped<IStochasticRunner, StochasticRunner>();
                services.TryAddScoped<IModelComparer>(sp => 
                    new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
                break;
            case ServiceLifetime.Transient:
                services.TryAddTransient<IStochasticRunner, StochasticRunner>();
                services.TryAddTransient<IModelComparer>(sp => 
                    new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
                break;
        }

        // Register CalibratedJudge factory (parameterized construction)
        services.TryAddSingleton<Func<IEnumerable<(string Name, IChatClient Client)>, CalibratedJudgeOptions?, ICalibratedJudge>>(
            _ => (judges, judgeOptions) => new CalibratedJudge(judges, judgeOptions));

        // Register CalibratedEvaluator factory (parameterized construction)
        services.TryAddSingleton<Func<IEnumerable<(string Name, IChatClient Client)>, CalibratedJudgeOptions?, IEvaluator>>(
            _ => (judges, judgeOptions) => new CalibratedEvaluator(judges, judgeOptions));

        // Register evaluation harness if provided
        if (options.EvaluationHarnessFactory != null)
        {
            services.TryAddSingleton(options.EvaluationHarnessFactory);
        }

        // Register logger if provided
        if (options.LoggerFactory != null)
        {
            services.TryAddSingleton(options.LoggerFactory);
        }

        return services;
    }

    /// <summary>
    /// Adds AgentEval core services with scoped lifetime (recommended for most applications).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalScoped(this IServiceCollection services)
    {
        return services.AddAgentEval(options => options.ServiceLifetime = ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Adds AgentEval core services with singleton lifetime (for long-running processes).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalSingleton(this IServiceCollection services)
    {
        return services.AddAgentEval(options => options.ServiceLifetime = ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Adds AgentEval core services with transient lifetime (creates new instance per request).
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalTransient(this IServiceCollection services)
    {
        return services.AddAgentEval(options => options.ServiceLifetime = ServiceLifetime.Transient);
    }
}
