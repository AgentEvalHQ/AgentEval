// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core.Benchmarks;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval bench --list</c>: enumerates every registered
/// <see cref="BenchmarkFamily"/> from <see cref="BenchmarkFamilyRegistry"/> and prints
/// a formatted table to stdout. Phase 8 / v0.10.0-beta.
/// </summary>
/// <remarks>
/// <b>Critical convention</b>: this command does NOT contain a hardcoded list of
/// families. Every family must register itself with <see cref="BenchmarkFamilyRegistry"/>
/// via a <c>[ModuleInitializer]</c> hook in its owning assembly. The CLI's only
/// responsibility is to <b>anchor</b> each expected assembly (touch one of its types
/// to force CLR module load) before enumerating <see cref="BenchmarkFamilyRegistry.All"/>.
/// </remarks>
public static class BenchListCommand
{
    /// <summary>Anchors every benchmark-bearing assembly to force module-initializer execution, then prints the list.</summary>
    public static int Run(TextWriter? writer = null)
    {
        writer ??= Console.Out;

        // ── Anchor assemblies so module initializers have fired ─────────────────
        // For each family we expect to find in --list, touch one type from that
        // assembly. The CLR JITs the module initializer the first time anything
        // in the module is accessed.
        AnchorAssemblies();

        var families = BenchmarkFamilyRegistry.All;

        if (families.Count == 0)
        {
            writer.WriteLine("No benchmark families registered.");
            writer.WriteLine("This is unexpected — at least 8 families should auto-register on assembly load.");
            return 1;
        }

        // ── Print header + table ──────────────────────────────────────────────
        writer.WriteLine("Registered benchmark families:");
        writer.WriteLine();

        var nameWidth = Math.Max(8, families.Max(f => f.Name.Length));
        var tierWidth = 7;  // "Medium "

        var header = $"  {"NAME".PadRight(nameWidth)}  {"TIER".PadRight(tierWidth)}  DESCRIPTION";
        writer.WriteLine(header);
        writer.WriteLine(new string('-', header.Length));

        foreach (var family in families)
        {
            writer.WriteLine($"  {family.Name.PadRight(nameWidth)}  {family.DefaultCostTier,-7}  {family.Description}");

            foreach (var preset in family.Presets)
            {
                var presetTier = preset.CostTier?.ToString() ?? "-";
                writer.WriteLine($"  {new string(' ', nameWidth)}  {presetTier,-7}  • {preset.Name}: {preset.Description}");
            }
            writer.WriteLine();
        }

        writer.WriteLine($"({families.Count} families registered)");
        writer.WriteLine();
        writer.WriteLine("Run `agenteval bench {family} --help` for per-family options.");

        return 0;
    }

    /// <summary>
    /// Anchor every benchmark-bearing assembly so its <c>[ModuleInitializer]</c> has fired
    /// before <see cref="BenchmarkFamilyRegistry.All"/> is enumerated. The harness MUST
    /// not depend on this being called only once; the registry's <c>Register</c> is
    /// idempotent for same-content registrations.
    /// </summary>
    internal static void AnchorAssemblies()
    {
        _ = typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.EuAiActBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.OwaspBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.MitreBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.PerformanceBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.MemoryBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.LongMemEvalBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.TraceFidelityBenchmark).Assembly;   // Glass Box (lives in AgentEval.Core)
    }
}
