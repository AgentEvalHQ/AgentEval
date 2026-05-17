// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;

namespace AgentEval.RedTeam.Compliance;

/// <summary>
/// Module-initializer hook that registers <see cref="MitreBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// Hybrid Shape A / Shape B registration mirroring <see cref="OwaspBenchmarkRegistration"/>
/// — see that type for the rationale.
/// </remarks>
internal static class MitreBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "mitre",
            description: "MITRE ATLAS red-team benchmark (6 of 8 applicable techniques covered)",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("atlas-baseline", "All 9 implemented attacks at Quick intensity (default)", CostTier.Medium),
                new("atlas-smoke", "3 MVP attacks at Quick intensity — CI-friendly", CostTier.Low),
                new("atlas-audit-grade", "All 9 attacks at Comprehensive intensity — audit-grade evidence", CostTier.High),
            ],
            runnerType: typeof(MitreBenchmarkRun),
            runnerFactory: preset => ResolvePresetRun(preset, judge: null),
            evaluateAsync: async (input, judge, ct) =>
            {
                var presetName = (input.Metadata?.TryGetValue("preset", out var p) == true) ? p?.ToString() ?? "atlas-baseline" : "atlas-baseline";
                var run = ResolvePresetRun(presetName, judge);
                return await run.EvaluateAsync(input, ct);
            },
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/redteam/mitre.md",
            owningAssemblyName: typeof(MitreBenchmark).Assembly.GetName().Name));
    }

    /// <summary>Resolves a preset name into the corresponding <see cref="MitreBenchmarkRun"/>.</summary>
    internal static MitreBenchmarkRun ResolvePresetRun(string presetName, IEvaluator? judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        return presetName.Trim().ToLowerInvariant() switch
        {
            "atlas-baseline"                            => MitreBenchmark.AtlasBaseline(judge),
            "atlas-smoke"                               => MitreBenchmark.AtlasSmoke(judge),
            "atlas-audit-grade" or "atlas-audit" or "audit" => MitreBenchmark.AtlasAuditGrade(judge),
            _ => throw new ArgumentException(
                $"Unknown MITRE preset '{presetName}'. Known presets: atlas-baseline, atlas-smoke, atlas-audit-grade.")
        };
    }
}
