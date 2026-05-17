// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;

namespace AgentEval.Compliance.EuAiAct;

/// <summary>
/// Module-initializer hook that registers <see cref="EuAiActBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// Shape A registration. The Smoke / Standard / AuditGrade presets build an
/// <see cref="EuAiActArticlesRegistry"/> internally from the supplied judge. Domain-pack
/// presets (HighRiskEmployment / Credit / Education) require a programmatic
/// <see cref="ScenarioToAtomicEval"/> configuration and are not exposed via the registry's
/// <c>CompositeFactory</c>; programmatic callers can still build them via the corresponding
/// <see cref="EuAiActBenchmark"/> methods.
/// </remarks>
internal static class EuAiActBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "eu-ai-act",
            description: "EU AI Act compliance benchmark — articles across 6 pillars",
            defaultCostTier: CostTier.High,
            presets:
            [
                new("smoke", "5 representative controls (CI-friendly, threshold 0.80)", CostTier.Medium),
                new("standard", "All articles across 6 pillars (threshold 0.85)", CostTier.High),
                new("audit", "Audit-grade with CapByWorst aggregation (threshold 0.90)", CostTier.High),
            ],
            compositeFactory: (preset, judge) => BuildPreset(preset, RequireJudge(judge, preset)),
            evaluateAsync: null,
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/compliance/eu-ai-act.md",
            owningAssemblyName: typeof(EuAiActBenchmark).Assembly.GetName().Name));
    }

    private static CompositeEval BuildPreset(string preset, IEvaluator judge)
    {
        var loader = new ArticleScenarioYamlLoader();
        var scenarioBuilder = new ScenarioToAtomicEval(judge);
        var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
        var articles = new EuAiActArticlesRegistry(loader, articleBuilder);

        return preset.Trim().ToLowerInvariant() switch
        {
            "smoke"                  => EuAiActBenchmark.Smoke(articles),
            "standard"               => EuAiActBenchmark.Standard(articles),
            "audit" or "auditgrade"  => EuAiActBenchmark.AuditGrade(articles),
            _ => throw new ArgumentException(
                $"Unknown EU AI Act preset '{preset}'. Known presets: smoke, standard, audit.")
        };
    }

    private static IEvaluator RequireJudge(IEvaluator? judge, string preset)
    {
        if (judge is null)
        {
            throw new ArgumentException(
                $"EU AI Act preset '{preset}' requires a non-null IEvaluator judge.");
        }
        return judge;
    }
}
