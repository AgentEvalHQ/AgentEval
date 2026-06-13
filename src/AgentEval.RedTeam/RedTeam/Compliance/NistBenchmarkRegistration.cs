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
/// Module-initializer hook that registers <see cref="NistBenchmark"/> with <see cref="BenchmarkFamilyRegistry"/> on
/// assembly load (Convention 3), mirroring <see cref="MitreBenchmarkRegistration"/>. Lives in the same
/// <c>AgentEval.RedTeam</c> assembly as OWASP/MITRE, so it appears in <c>bench --list</c> automatically.
/// </summary>
internal static class NistBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "nist",
            description: "NIST AI RMF (AI 100-1) red-team evidence — MEASURE security/privacy/validity sub-actions (GOVERN/MAP/MANAGE are Not Applicable)",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("rmf-baseline", "All 13 built-in attacks at Quick intensity (default)", CostTier.Medium),
                new("rmf-smoke", "3 MVP attacks at Quick intensity — CI-friendly", CostTier.Low),
                new("rmf-audit-grade", "All 13 attacks at Comprehensive intensity — audit-grade evidence", CostTier.High),
            ],
            runnerType: typeof(NistBenchmarkRun),
            runnerFactory: preset => ResolvePresetRun(preset, judge: null),
            evaluateAsync: async (input, judge, ct) =>
            {
                var presetName = (input.Metadata?.TryGetValue("preset", out var p) == true) ? p?.ToString() ?? "rmf-baseline" : "rmf-baseline";
                var run = ResolvePresetRun(presetName, judge);
                return await run.EvaluateAsync(input, ct);
            },
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/redteam.md",
            owningAssemblyName: typeof(NistBenchmark).Assembly.GetName().Name));
    }

    /// <summary>Resolves a preset name into the corresponding <see cref="NistBenchmarkRun"/>.</summary>
    internal static NistBenchmarkRun ResolvePresetRun(string presetName, IEvaluator? judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        return presetName.Trim().ToLowerInvariant() switch
        {
            "rmf-baseline"                          => NistBenchmark.RmfBaseline(judge),
            "rmf-smoke"                             => NistBenchmark.RmfSmoke(judge),
            "rmf-audit-grade" or "rmf-audit" or "audit" => NistBenchmark.RmfAuditGrade(judge),
            _ => throw new ArgumentException(
                $"Unknown NIST preset '{presetName}'. Known presets: rmf-baseline, rmf-smoke, rmf-audit-grade.")
        };
    }
}
