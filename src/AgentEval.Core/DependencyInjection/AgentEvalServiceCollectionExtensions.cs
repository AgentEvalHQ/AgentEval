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

        // Register IAgentEvalEmbeddings: wraps IEmbeddingGenerator if available (G4),
        // optionally behind CachingEmbeddingsDecorator when EnableEmbeddingCaching is true.
        // Honor options.ServiceLifetime (default Scoped) rather than forcing Singleton: a
        // Singleton wrapping a Scoped/Transient IEmbeddingGenerator (common in ASP.NET hosting)
        // becomes a captive dependency that throws under validateScopes (BUG-41). With caching
        // enabled the cache then follows the chosen lifetime (per-scope for Scoped).
        //
        // Validate the cache bound before wiring the decorator — a zero/negative value bound from
        // config would otherwise flow through silently and disable/break caching (GAP-18).
        if (options.EnableEmbeddingCaching && options.EmbeddingCacheMaxSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options.EmbeddingCacheMaxSize), options.EmbeddingCacheMaxSize,
                "EmbeddingCacheMaxSize must be greater than 0 when embedding caching is enabled.");

        Func<IServiceProvider, object> embeddingsFactory = options.EnableEmbeddingCaching
            ? sp => new CachingEmbeddingsDecorator(
                new MEAIEmbeddingAdapter(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()),
                options.EmbeddingCacheMaxSize)
            : sp => new MEAIEmbeddingAdapter(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        services.TryAdd(new ServiceDescriptor(typeof(IAgentEvalEmbeddings), embeddingsFactory, options.ServiceLifetime));

        // Register IEvaluator: default ChatClientEvaluator using IChatClient (G9). Same configured
        // lifetime, so the evaluator does not capture a Scoped/Transient IChatClient (BUG-41).
        services.TryAdd(new ServiceDescriptor(
            typeof(IEvaluator),
            sp => new ChatClientEvaluator(sp.GetRequiredService<IChatClient>()),
            options.ServiceLifetime));

        // Register runners and comparers with appropriate lifetimes.
        // IStochasticRunner is built via a factory that resolves IEvaluationHarness with a clear,
        // actionable error when it is absent — instead of the cryptic "Unable to resolve
        // IEvaluationHarness" at first resolve. The harness has no default in core (it lives in the
        // MAF package), and registration order is host-controlled, so this is fail-fast-at-resolve
        // with guidance rather than a registration-time guard that could race the harness (GAP-08).
        static IStochasticRunner BuildRunner(IServiceProvider sp) =>
            new StochasticRunner(
                ResolveHarnessOrThrow(sp),
                sp.GetService<IStatisticsCalculator>(),
                sp.GetService<EvaluationOptions>());
        switch (options.ServiceLifetime)
        {
            case ServiceLifetime.Singleton:
                services.TryAddSingleton<IStochasticRunner>(BuildRunner);
                services.TryAddSingleton<IModelComparer>(sp =>
                    new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
                break;
            case ServiceLifetime.Scoped:
                services.TryAddScoped<IStochasticRunner>(BuildRunner);
                services.TryAddScoped<IModelComparer>(sp =>
                    new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
                break;
            case ServiceLifetime.Transient:
                services.TryAddTransient<IStochasticRunner>(BuildRunner);
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

    /// <summary>
    /// Resolves <see cref="IEvaluationHarness"/> or throws a clear, actionable error explaining how
    /// to register one. IStochasticRunner/IModelComparer depend on it, but the core package ships no
    /// default harness (it lives in the MAF package), so a parameterless AddAgentEval otherwise fails
    /// with a cryptic DI error at first resolve (GAP-08).
    /// </summary>
    private static IEvaluationHarness ResolveHarnessOrThrow(IServiceProvider sp)
        => sp.GetService<IEvaluationHarness>()
           ?? throw new InvalidOperationException(
               "AgentEval's IStochasticRunner/IModelComparer require an IEvaluationHarness, but none is " +
               "registered. Register one before resolving — e.g. call services.AddAgentEvalMaf() (MAF package), " +
               "set AgentEvalServiceOptions.EvaluationHarnessFactory, or register your own via " +
               "services.AddSingleton<IEvaluationHarness>(...).");
}
