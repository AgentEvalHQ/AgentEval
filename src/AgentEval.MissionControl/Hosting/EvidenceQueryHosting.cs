// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using AgentEval.Results.Suites;
using AgentEval.Results.Signatures;
using AgentEval.Results.Requirements;
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
            builder.Services.TryAddSingleton<IResultDetailQueryService>(_ => new ResultDetailQueryService());
            builder.Services.TryAddSingleton<IGovernanceQueryService>(_ => new GovernanceQueryService());
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
            builder.Services.TryAddSingleton<IResultDetailQueryService>(_ => new ResultDetailQueryService());
            builder.Services.TryAddSingleton<IGovernanceQueryService>(_ => new GovernanceQueryService());
            return;
        }
        var roots = configuredRoots.Select(root => new EvidenceIndexRoot(root["RootId"]!, root["Directory"]!, IndexedEvidenceKind.Aef)).ToArray();
        var plan = new EvidenceIndexRootPlanner().Plan(roots);
        roots = plan.ScanRoots.Concat(plan.CoveredRoots.Select(root => root.Root)).ToArray();
        var policyPath = builder.Configuration["AgentEval:Queries:TrustPolicyPath"];
        if (!string.IsNullOrWhiteSpace(policyPath))
            builder.Services.TryAddSingleton(TrustPolicy.Parse(ReadConfiguration(policyPath)));
        builder.Services.TryAddSingleton<IEvidenceSourceLookup>(_ => new SqliteEvidenceSourceLookup(options));
        builder.Services.TryAddSingleton<ISuiteIdentityReader, SuiteIdentityReader>();
        builder.Services.TryAddSingleton<ISuiteCompatibilityEvaluator, SuiteCompatibilityEvaluator>();
        builder.Services.TryAddSingleton<IAefResultDetailReader, AefResultDetailReader>();
        builder.Services.TryAddSingleton<IResultDetailQueryService>(sp => new ResultDetailQueryService(
            sp.GetRequiredService<IEvidenceSourceLookup>(), sp.GetRequiredService<IAefResultDetailReader>(),
            roots, sp.GetService<TrustPolicy>()));
        builder.Services.TryAddSingleton<ISuiteQueryService>(sp => new SuiteQueryService(
            sp.GetRequiredService<IEvidenceSourceLookup>(), sp.GetRequiredService<ISuiteIdentityReader>(),
            sp.GetRequiredService<ISuiteCompatibilityEvaluator>(), roots));
        var cataloguePath = builder.Configuration["AgentEval:Queries:CataloguePath"];
        if (string.IsNullOrWhiteSpace(cataloguePath))
        {
            builder.Services.TryAddSingleton<IGovernanceQueryService>(_ => new GovernanceQueryService());
            return;
        }
        var catalogue = new RequirementCatalogueReader().Read(ReadConfiguration(cataloguePath));
        var limitsSection = builder.Configuration.GetSection("AgentEval:Queries:GovernanceLimits");
        var limits = new GovernanceQueryLimits
        {
            MaxSources = limitsSection.GetValue("MaxSources", 8),
            MaxCaptureBytes = limitsSection.GetValue("MaxCaptureBytes", 64L << 20),
            MaxRowRulePairs = limitsSection.GetValue("MaxRowRulePairs", 100_000L),
            MaxResponseBytes = limitsSection.GetValue("MaxResponseBytes", 2 << 20)
        };
        builder.Services.TryAddSingleton<IRequirementEvidenceReader, RequirementEvidenceReader>();
        builder.Services.TryAddSingleton<IBoundedRequirementEvidenceReader, BoundedRequirementEvidenceReader>();
        builder.Services.TryAddSingleton<IRequirementStatusResolver, RequirementStatusResolver>();
        builder.Services.TryAddSingleton<IGovernanceQueryService>(sp => new GovernanceQueryService(
            sp.GetRequiredService<IEvidenceSourceLookup>(), sp.GetRequiredService<IBoundedRequirementEvidenceReader>(),
            sp.GetRequiredService<IRequirementStatusResolver>(), catalogue, roots, sp.GetService<TrustPolicy>(), limits));
    }

    private static byte[] ReadConfiguration(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Expected: absolute query configuration path. Actual: relative path. Suggestions: configure an explicit JSON file. Because: host authority must not depend on the working directory.");
        using var input = File.OpenRead(path);
        var bytes = new byte[RequirementCatalogueReader.MaxBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = input.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total == bytes.Length)
            throw new InvalidOperationException("Expected: query configuration at most 1 MiB. Actual: file exceeds the limit. Suggestions: reduce the catalogue or trust policy. Because: configuration loading is bounded.");
        return bytes.AsSpan(0, total).ToArray();
    }
}
