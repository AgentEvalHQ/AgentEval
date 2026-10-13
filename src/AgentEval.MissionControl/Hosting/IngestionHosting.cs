// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;
using Path = System.IO.Path;

namespace AgentEval.MissionControl.Hosting;

internal static class IngestionHosting
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        if (!configuration.GetValue<bool>("AgentEval:Ingestion:Enabled")) return;

        builder.Services.AddSingleton<IAefRunDiscovery, AefRunDiscovery>();
        builder.Services.AddSingleton(_ => IngestionHttpOptions.Read(configuration));
        builder.Services.AddSingleton<IAefRunStager>(sp => new AefRunStager(
            sp.GetRequiredService<IAefIngestionWorkspace>(), sp.GetRequiredService<IngestionHttpOptions>().Staging));
        builder.Services.AddSingleton<IAefRunFinalizer>(sp => new AefRunFinalizer(
            sp.GetRequiredService<IAefIngestionWorkspace>(), sp.GetRequiredService<IngestionHttpOptions>().Staging));
        builder.Services.AddSingleton<IAefLocalRunImporter>(sp => new AefLocalRunImporter(
            sp.GetRequiredService<IAefRunStager>(), sp.GetRequiredService<IngestionHttpOptions>().Staging));
        builder.Services.AddSingleton<IAefIngestionRecovery>(sp => new AefIngestionRecovery(
            sp.GetRequiredService<IAefIngestionWorkspace>(), sp.GetRequiredService<IAefRunFinalizer>(),
            sp.GetRequiredService<IngestionHttpOptions>().Staging));
        builder.Services.AddSingleton<IAefIngestionWorkspace>(_ =>
        {
            var root = configuration["AgentEval:Ingestion:Root"];
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("Expected: an absolute AgentEval:Ingestion:Root. Actual: missing or relative root. " +
                    "Suggestions: configure a dedicated ingestion directory. Because: enabling ingestion must not enable writes to the viewer store.");

            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var configuredViewer = configuration["AgentEval:Root"];
            var viewer = Path.Combine(string.IsNullOrWhiteSpace(configuredViewer)
                ? McHost.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory()) : configuredViewer, ".agenteval");
            viewer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(viewer));
            if (Contains(root, viewer) || Contains(viewer, root))
                throw new InvalidOperationException("Expected: disjoint ingestion and viewer storage. Actual: overlapping roots. " +
                    "Suggestions: choose a separate ingestion directory. Because: the existing output store remains read-only to Mission Control.");

            return new AefIngestionWorkspace(new()
            {
                Root = root,
                MaxPendingStages = configuration.GetValue("AgentEval:Ingestion:MaxPendingStages", 64),
                MaxAcceptedRuns = configuration.GetValue("AgentEval:Ingestion:MaxAcceptedRuns", 1_000),
                MaxArchivedReceipts = configuration.GetValue("AgentEval:Ingestion:MaxArchivedReceipts", 10_000)
            });
        });
        // Force validation and ownership acquisition at startup, before requests can reach the host.
        // The container owns disposal of the factory-created singleton and releases its lease.
        builder.Services.AddHostedService<IngestionLifetime>();
    }

    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) || child.StartsWith(
            Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private sealed class IngestionLifetime(IngestionHttpOptions options, IAefRunStager stager, IAefIngestionWorkspace workspace) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = options.TimeoutSeconds;
            _ = stager;
            _ = workspace.Root;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
