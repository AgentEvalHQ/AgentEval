// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.Exporters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.DependencyInjection;

/// <summary>
/// Extension methods for registering AgentEval DataLoader and Exporter services.
/// </summary>
public static class DataLoaderServiceCollectionExtensions
{
    /// <summary>
    /// Adds AgentEval DataLoader and Exporter services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentEvalDataLoaders(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Ensure core services are registered (idempotent via TryAdd*)
        services.AddAgentEval();

        // Register dataset loader factory as singleton (stateless, thread-safe)
        services.TryAddSingleton<IDatasetLoaderFactory>(sp =>
            new DefaultDatasetLoaderFactory(sp.GetServices<IDatasetLoader>()));

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

        return services;
    }
}
