// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.Embeddings;
using AgentEval.Exporters;
using AgentEval.RedTeam;
using AgentEval.Snapshots;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Extension methods for configuring AgentEval services in an <see cref="IServiceCollection"/>.
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

        // Register dataset loader factory as singleton (stateless, thread-safe)
        // Uses the constructor that accepts DI-registered IDatasetLoader instances
        services.TryAddSingleton<IDatasetLoaderFactory>(sp =>
            new DefaultDatasetLoaderFactory(sp.GetServices<IDatasetLoader>()));

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

        // Register IExporterRegistry: pre-populated with built-in exporters
        // + auto-populates from DI-registered IResultExporter services
        services.TryAddSingleton<IExporterRegistry>(sp =>
        {
            var registry = new ExporterRegistry();
            // Built-in exporters
            registry.Register("Json", new JsonExporter());
            registry.Register("Junit", new JUnitXmlExporter());
            registry.Register("Markdown", new MarkdownExporter());
            registry.Register("Csv", new CsvExporter());
            registry.Register("Trx", new TrxExporter());
            // Extension-registered exporters from DI
            foreach (var exporter in sp.GetServices<IResultExporter>())
            {
                var name = exporter.FormatName;
                if (!registry.Contains(name))
                {
                    registry.Register(name, exporter);
                }
            }
            return registry;
        });

        // Register IAttackTypeRegistry: pre-populated with 9 built-in attacks
        // + auto-populates from DI-registered IAttackType services
        services.TryAddSingleton<IAttackTypeRegistry>(sp =>
            new AttackTypeRegistry(sp.GetServices<IAttackType>()));

        // Register IAgentEvalEmbeddings: wraps IEmbeddingGenerator if available (G4)
        // Users who need embedding metrics must register IEmbeddingGenerator<string, Embedding<float>>
        // before calling AddAgentEval(), or register their own IAgentEvalEmbeddings directly.
        // TryAdd ensures user-registered IAgentEvalEmbeddings is not overridden.
        services.TryAddSingleton<IAgentEvalEmbeddings>(sp =>
            new MEAIEmbeddingAdapter(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        // Register IEvaluator: default ChatClientEvaluator using IChatClient (G9)
        // Users who need response evaluation must register IChatClient before calling AddAgentEval(),
        // or register their own IEvaluator directly. TryAdd ensures user overrides are respected.
        services.TryAddSingleton<IEvaluator>(sp =>
            new ChatClientEvaluator(sp.GetRequiredService<IChatClient>()));

        // Register runners and comparers with appropriate lifetimes
        // Use factory for ModelComparer to avoid ambiguous constructor issues
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

        // Register CalibratedEvaluator factory (parameterized construction — same pattern as CalibratedJudge)
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
