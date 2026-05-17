// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;

namespace AgentEval.Compliance.Gdpr;

/// <summary>
/// Module-initializer hook that registers <see cref="GdprBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// Shape A registration. The Smoke / Standard / AuditGrade presets build an
/// <see cref="ArticlesRegistry"/> internally from the supplied judge (mirrors what
/// <c>BenchCommand.RunGdprAsync</c> does today). The Healthcare / HR / ChildrensService
/// presets are not exposed via the registry's <c>CompositeFactory</c> — they require a
/// <see cref="ScenarioToAtomicEval"/> with domain-pack-specific configuration that is
/// awkward to wire through a string preset selector. Programmatic callers can still build
/// them via <see cref="GdprBenchmark.Healthcare(ArticlesRegistry, ScenarioToAtomicEval)"/>.
/// </para>
/// </remarks>
internal static class GdprBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "gdpr",
            description: "GDPR compliance benchmark — 21 articles across 5 pillars",
            defaultCostTier: CostTier.High,
            presets:
            [
                new("smoke", "5 representative articles (CI-friendly, threshold 0.80)", CostTier.Medium),
                new("standard", "All 21 articles across 5 pillars (threshold 0.85)", CostTier.High),
                new("audit", "Audit-grade with CapByWorst aggregation (threshold 0.90)", CostTier.High),
            ],
            compositeFactory: (preset, judge) => BuildPreset(preset, RequireJudge(judge, preset)),
            evaluateAsync: null,  // CompositeEval-native; consumers call CompositeEval.EvaluateAsync.
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/compliance/gdpr.md",
            owningAssemblyName: typeof(GdprBenchmark).Assembly.GetName().Name));
    }

    private static CompositeEval BuildPreset(string preset, IEvaluator judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge);
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        var articles = new ArticlesRegistry(loader, articleBuilder);

        return preset.Trim().ToLowerInvariant() switch
        {
            "smoke"                  => GdprBenchmark.Smoke(articles),
            "standard"               => GdprBenchmark.Standard(articles),
            "audit" or "auditgrade"  => GdprBenchmark.AuditGrade(articles),
            _ => throw new ArgumentException(
                $"Unknown GDPR preset '{preset}'. Known presets: smoke, standard, audit. " +
                "Domain-pack presets (healthcare, hr, childrens-service) require a programmatic " +
                "ScenarioToAtomicEval configuration and are not exposed via the registry's CompositeFactory.")
        };
    }

    private static IEvaluator RequireJudge(IEvaluator? judge, string preset)
    {
        if (judge is null)
        {
            throw new ArgumentException(
                $"GDPR preset '{preset}' requires a non-null IEvaluator judge.");
        }
        return judge;
    }
}
