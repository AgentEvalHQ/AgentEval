// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core.Benchmarks;

namespace AgentEval.Benchmarks;

/// <summary>
/// Module-initializer hook that registers <see cref="WorkflowTraceFidelityBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (ADR-017 Convention 3). Shape B — a custom
/// per-executor reconciler, so no <c>CompositeFactory</c> and no Convention-2 <c>evaluateAsync</c> adapter.
/// Lives in the same assembly as the single-agent trace-fidelity family; no CLI anchor change is needed.
/// </summary>
internal static class WorkflowTraceFidelityBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register() => BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
        name: "workflow-trace-fidelity",
        description: "Reconciles each workflow executor's framework-reported per-executor ledger (tokens + finish reason) against chat-boundary truth; reports per-executor fidelity (Agree / TokenMismatch / FinishMismatch / NoTruth).",
        defaultCostTier: CostTier.Free,   // pure code, no LLM tokens
        presets:
        [
            new("smoke", "Per-executor ledger vs chat truth", CostTier.Free),
            new("standard", "Per-executor ledger vs chat truth", CostTier.Free),
            new("audit-grade", "Per-executor ledger vs chat truth", CostTier.Free),
        ],
        runnerType: typeof(WorkflowTraceFidelityReconciler),
        runnerFactory: preset => preset.Trim().ToLowerInvariant() switch
        {
            "smoke" => WorkflowTraceFidelityBenchmark.Smoke(),
            "standard" => WorkflowTraceFidelityBenchmark.Standard(),
            "audit-grade" => WorkflowTraceFidelityBenchmark.AuditGrade(),
            _ => throw new ArgumentException($"Unknown workflow-trace-fidelity preset '{preset}'. Known: smoke, standard, audit-grade."),
        },
        evaluateAsync: null,   // Shape B — WorkflowExecutionResult + per-executor traces don't map onto a single EvalInput
        docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/benchmarks/workflow-trace-fidelity.md",
        owningAssemblyName: typeof(WorkflowTraceFidelityBenchmark).Assembly.GetName().Name));
}
