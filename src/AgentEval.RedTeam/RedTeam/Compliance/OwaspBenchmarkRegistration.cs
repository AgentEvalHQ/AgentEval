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
/// Module-initializer hook that registers <see cref="OwaspBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// Hybrid Shape A / Shape B registration. Each preset returns an
/// <see cref="OwaspBenchmarkRun"/> (Shape B / runner-like), but the run also exposes a
/// Convention-2 <c>EvaluateAsync</c> adapter that produces a 10-leaf
/// <see cref="EvalResult"/> composite. So the registry wires both:
/// </para>
/// <list type="bullet">
///   <item><see cref="BenchmarkFamily.CompositeFactory"/>: returns a synthetic CompositeEval
///         wrapper whose <c>EvaluateAsync</c> delegates to the run's existing adapter.</item>
///   <item><see cref="BenchmarkFamily.RunnerFactory"/> + <see cref="BenchmarkFamily.RunnerType"/>:
///         returns the <see cref="OwaspBenchmarkRun"/> directly for callers who want access
///         to <c>ScanAsync</c> / <c>GenerateReport</c>.</item>
///   <item><see cref="BenchmarkFamily.EvaluateAsync"/>: direct Convention-2 adapter.</item>
/// </list>
/// </remarks>
internal static class OwaspBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "owasp",
            description: "OWASP LLM Top 10 v2.0 red-team benchmark (all 10 categories covered — Wave D)",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("top10", "All 13 built-in attacks at Quick intensity (default)", CostTier.Medium),
                new("smoke", "3 MVP attacks (PromptInjection + Jailbreak + PIILeakage) — CI-friendly", CostTier.Low),
                new("audit", "All 13 attacks at Comprehensive intensity — audit-grade evidence", CostTier.High),
                new("top10-rag", "All 13 attacks at Comprehensive intensity, 20-min timeout — RAG-vector depth (LLM01 indirect-injection + LLM08 vector-embedding emphasis)", CostTier.High),
            ],
            // Shape B (primary): return the OwaspBenchmarkRun directly so consumers can call ScanAsync / GenerateReport.
            runnerType: typeof(OwaspBenchmarkRun),
            runnerFactory: preset => ResolvePresetRun(preset, judge: null),
            // Shape A: a synthetic composite factory is omitted — the natural shape is Shape B.
            // EvaluateAsync: delegate to OwaspBenchmarkRun.EvaluateAsync (the Phase-5 adapter).
            evaluateAsync: async (input, judge, ct) =>
            {
                // Default preset for the registry's EvaluateAsync path is "top10".
                // Callers needing a different preset use runnerFactory directly.
                var presetName = (input.Metadata?.TryGetValue("preset", out var p) == true) ? p?.ToString() ?? "top10" : "top10";
                var run = ResolvePresetRun(presetName, judge);
                return await run.EvaluateAsync(input, ct);
            },
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/redteam/owasp.md",
            owningAssemblyName: typeof(OwaspBenchmark).Assembly.GetName().Name));
    }

    /// <summary>Resolves a preset name into the corresponding <see cref="OwaspBenchmarkRun"/>.</summary>
    internal static OwaspBenchmarkRun ResolvePresetRun(string presetName, IEvaluator? judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        return presetName.Trim().ToLowerInvariant() switch
        {
            "top10"                          => OwaspBenchmark.Top10(judge),
            "smoke"                          => OwaspBenchmark.Smoke(judge),
            "audit" or "auditgrade"          => OwaspBenchmark.AuditGrade(judge),
            "top10-rag" or "top10forrag"     => OwaspBenchmark.Top10ForRag(judge),
            _ => throw new ArgumentException(
                $"Unknown OWASP preset '{presetName}'. Known presets: top10, smoke, audit, top10-rag.")
        };
    }
}
