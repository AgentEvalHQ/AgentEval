// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;

namespace AgentEval.Memory;

/// <summary>
/// Module-initializer hook that registers <see cref="MemoryBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape B registration</b>. The Memory family's natural shape is a multi-turn, multi-
/// scenario runner (<see cref="AgentEval.Memory.Evaluators.MemoryBenchmarkRunner"/>) driven
/// by a <see cref="MemoryBenchmark"/> config record. The registry's <c>RunnerFactory</c>
/// returns the <see cref="MemoryBenchmark"/> preset record directly — callers obtain a
/// runner instance separately and combine the two via
/// <c>runner.RunBenchmarkAsync(agent, preset)</c>.
/// </para>
/// <para>
/// <b>No EvaluateAsync adapter</b>: Memory's "multi-scenario, multi-turn, agent-stateful"
/// semantics are not a fit for the single-shot Convention-2 shape. Mission Control renders
/// the native <c>MemoryBenchmarkResult</c> directly.
/// </para>
/// </remarks>
internal static class MemoryBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "memory",
            description: "Memory benchmark — agent-state-stressing scenarios across retention / temporal / context-pressure dimensions",
            defaultCostTier: CostTier.High,
            presets:
            [
                new("quick", "3 categories (basic retention, temporal, noise) — CI-friendly", CostTier.Medium),
                new("standard", "8 categories including Abstention + Preference Extraction", CostTier.High),
                new("full", "12 categories including cross-session, conflict resolution, multi-session reasoning", CostTier.High),
                new("diagnostic", "Same categories as Full with maximum context pressure (~50K+ tokens)", CostTier.High),
                new("overflow", "8 categories with context overflow (192K target on 128K window)", CostTier.High),
            ],
            // Shape B — RunnerFactory returns the preset record; caller pairs with their MemoryBenchmarkRunner.
            runnerType: typeof(MemoryBenchmark),
            runnerFactory: preset => ResolvePreset(preset),
            evaluateAsync: null,  // Shape B; no Convention-2 adapter (see remarks).
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/memory-benchmark.md",
            owningAssemblyName: typeof(MemoryBenchmark).Assembly.GetName().Name));
    }

    /// <summary>Resolves a preset name into the corresponding <see cref="MemoryBenchmark"/> record.</summary>
    internal static MemoryBenchmark ResolvePreset(string preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        return preset.Trim().ToLowerInvariant() switch
        {
            "quick"      => MemoryBenchmark.Quick,
            "standard"   => MemoryBenchmark.Standard,
            "full"       => MemoryBenchmark.Full,
            "diagnostic" => MemoryBenchmark.Diagnostic,
            "overflow"   => MemoryBenchmark.Overflow,
            _ => throw new ArgumentException(
                $"Unknown memory preset '{preset}'. Known presets: quick, standard, full, diagnostic, overflow.")
        };
    }
}
