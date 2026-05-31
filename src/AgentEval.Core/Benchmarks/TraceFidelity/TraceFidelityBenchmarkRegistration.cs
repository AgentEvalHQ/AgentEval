// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core.Benchmarks;

namespace AgentEval.Benchmarks;

/// <summary>
/// Module-initializer hook that registers <see cref="TraceFidelityBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (ADR-017 Convention 3). Shape B — a custom runner
/// consuming two traces, so no <c>CompositeFactory</c> and no Convention-2 <c>evaluateAsync</c> adapter.
/// </summary>
internal static class TraceFidelityBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register() => BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
        name: "trace-fidelity",
        description: "Reconciles agent-boundary vs chat-boundary traces; flags missing/phantom calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons.",
        defaultCostTier: CostTier.Free,   // pure code, no LLM tokens
        presets:
        [
            new("smoke", "Core discrepancy classes; de-dup on", CostTier.Free),
            new("standard", "All six discrepancy classes; de-dup on", CostTier.Free),
            new("audit-grade", "All six classes; verbatim payloads (de-dup off)", CostTier.Free),
        ],
        runnerType: typeof(TraceFidelityRunner),
        runnerFactory: preset => preset.Trim().ToLowerInvariant() switch
        {
            "smoke" => TraceFidelityBenchmark.Smoke(),
            "standard" => TraceFidelityBenchmark.Standard(),
            "audit-grade" => TraceFidelityBenchmark.AuditGrade(),
            _ => throw new ArgumentException($"Unknown trace-fidelity preset '{preset}'. Known: smoke, standard, audit-grade."),
        },
        evaluateAsync: null,   // Shape B — two-trace input doesn't map onto a single EvalInput
        docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/benchmarks/trace-fidelity.md",
        owningAssemblyName: typeof(TraceFidelityBenchmark).Assembly.GetName().Name));
}
