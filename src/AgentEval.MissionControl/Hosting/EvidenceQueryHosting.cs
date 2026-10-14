// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using AgentEval.Results.Suites;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.MissionControl.Hosting;

internal static class EvidenceQueryHosting
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("AgentEval:Queries:Enabled"))
        {
            builder.Services.TryAddSingleton<IEvidenceQueryService>(_ => new EvidenceQueryService());
            builder.Services.TryAddSingleton<ISuiteQueryService>(_ => new SuiteQueryService());
            return;
        }
        var path = builder.Configuration["AgentEval:Queries:IndexPath"];
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Expected: an absolute AgentEval:Queries:IndexPath. Actual: missing or relative path. Suggestions: configure the derived index explicitly. Because: read queries must not infer storage.");
        var options = new EvidenceIndexOptions { DatabasePath = System.IO.Path.GetFullPath(path) };
        builder.Services.TryAddSingleton<IEvidenceSourceReader>(_ => new SqliteEvidenceSourceReader(options));
        builder.Services.TryAddSingleton<IEvidenceDetailReader>(_ => new SqliteEvidenceDetailReader(options));
        builder.Services.TryAddSingleton<IEvidenceQueryService, EvidenceQueryService>();
        var configuredRoots = builder.Configuration.GetSection("AgentEval:Queries:AefRoots").GetChildren().Take(257).ToArray();
        if (configuredRoots.Length > 256)
            throw new InvalidOperationException("Expected: at most 256 AEF roots. Actual: too many configured roots. Suggestions: reduce the root set. Because: verified queries require bounded configuration.");
        if (configuredRoots.Length == 0)
        {
            builder.Services.TryAddSingleton<ISuiteQueryService>(_ => new SuiteQueryService());
            return;
        }
        var roots = configuredRoots.Select(root => new EvidenceIndexRoot(root["RootId"]!, root["Directory"]!, IndexedEvidenceKind.Aef)).ToArray();
        var plan = new EvidenceIndexRootPlanner().Plan(roots);
        roots = plan.ScanRoots.Concat(plan.CoveredRoots.Select(root => root.Root)).ToArray();
        builder.Services.TryAddSingleton<IEvidenceSourceLookup>(_ => new SqliteEvidenceSourceLookup(options));
        builder.Services.TryAddSingleton<ISuiteIdentityReader, SuiteIdentityReader>();
        builder.Services.TryAddSingleton<ISuiteCompatibilityEvaluator, SuiteCompatibilityEvaluator>();
        builder.Services.TryAddSingleton<ISuiteQueryService>(sp => new SuiteQueryService(
            sp.GetRequiredService<IEvidenceSourceLookup>(), sp.GetRequiredService<ISuiteIdentityReader>(),
            sp.GetRequiredService<ISuiteCompatibilityEvaluator>(), roots));
    }
}
