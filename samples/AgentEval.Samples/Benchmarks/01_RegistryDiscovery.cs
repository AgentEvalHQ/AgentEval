// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core.Benchmarks;
using AgentEval.Evals;

// Force-load every umbrella sub-assembly so each family's [ModuleInitializer]
// fires before we enumerate the registry. Touching one type per assembly is
// the canonical CLI pattern from `bench --list`.
using AgentEval.Benchmarks;                       // → AgentEval.Evals.Agentic
using AgentEval.Compliance.Gdpr.Articles;          // → AgentEval.Compliance.Gdpr
using AgentEval.Compliance.EuAiAct.Articles;       // → AgentEval.Compliance.EuAiAct
using AgentEval.RedTeam;                           // → AgentEval.RedTeam (OWASP/MITRE)
using AgentEval.Memory;                            // → AgentEval.Memory + LongMemEval

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B1: Registry Discovery — walk the canonical
/// <see cref="BenchmarkFamilyRegistry"/> and enumerate every registered family
/// + preset shipped by v0.10.x. Demonstrates the discovery surface that
/// <c>agenteval bench --list</c> and Mission Control build their family
/// pickers on top of.
///
/// No Azure OpenAI credentials required — this sample is pure code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Assembly-anchor trick</b>: <see cref="BenchmarkFamilyRegistry"/> is populated
/// lazily by <c>[ModuleInitializer]</c>-attributed methods. We force-load each
/// sub-assembly by referencing one type per family (the <c>using</c> lines above)
/// so all eight families are visible before <see cref="BenchmarkFamilyRegistry.All"/>
/// is enumerated.
/// </para>
/// </remarks>
public static class RegistryDiscoveryBenchmark
{
    public static Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B1: BenchmarkFamilyRegistry Discovery",
            "Walk every registered benchmark family + preset (no Azure required)");

        // Force-load: read `.Assembly` off one type per family. This is the canonical
        // anchor pattern (same idiom as `BenchListCommand.AnchorAssemblies`): JITting the
        // property access forces the CLR to resolve the type token, load the assembly,
        // and run its [ModuleInitializer] which registers the family with the registry.
        // `nameof(...)` would NOT achieve this — it is a compile-time constant.
        _ = typeof(AgenticBenchmark).Assembly;
        _ = typeof(ArticlesRegistry).Assembly;
        _ = typeof(EuAiActArticlesRegistry).Assembly;
        _ = typeof(AgentEval.Benchmarks.OwaspBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.MitreBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.PerformanceBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.MemoryBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.LongMemEvalBenchmark).Assembly;

        var families = BenchmarkFamilyRegistry.All;
        Console.WriteLine($"   {families.Count} benchmark families registered:\n");

        foreach (var family in families)
        {
            Console.WriteLine($"   • {family.Name,-14} ({family.DefaultCostTier,-6}) — {family.Description}");
            foreach (var preset in family.Presets)
            {
                var tier = preset.CostTier?.ToString() ?? family.DefaultCostTier.ToString();
                Console.WriteLine($"       └─ {preset.Name,-22} [{tier,-6}] {preset.Description}");
            }
            if (!string.IsNullOrEmpty(family.DocLinkUrl))
                Console.WriteLine($"       docs: {family.DocLinkUrl}");
            Console.WriteLine();
        }

        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - BenchmarkFamilyRegistry is the canonical discovery surface (ADR-017 Convention 3).");
        Console.WriteLine("   - Every shipped family auto-registers via [ModuleInitializer] on assembly load.");
        Console.WriteLine("   - Third-party plugins (e.g. AgentEval.Compliance.Hipaa) drop in via the same mechanism.");
        Console.WriteLine("   - For a CLI workflow, use `agenteval bench --list` — it touches every umbrella sub-assembly");
        Console.WriteLine("     so the registry is populated before enumeration.");

        return Task.CompletedTask;
    }
}
