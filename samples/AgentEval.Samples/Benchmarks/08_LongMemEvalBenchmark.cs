// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;                    // → LongMemEvalBenchmark / MemoryBenchmark
using AgentEval.Core.Benchmarks;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B8: LongMemEval — cross-platform memory evaluation
/// (ICLR 2025 paper, MIT-licensed dataset).
///
/// LongMemEval is a Shape B / runner-style benchmark (see ADR-017 Convention 3).
/// The <c>Subset()</c> preset (30 questions, embedded) is CI-friendly; the
/// <c>Full()</c> preset (~500 questions) requires the <c>LONGMEMEVAL_DATASET_PATH</c>
/// environment variable to be set.
///
/// This sample walks the registry metadata, points to the canonical entry points,
/// and prints how to obtain the full dataset. A real LongMemEval run needs a
/// memory-enabled agent (see Memory Evaluation samples, group G) — out of scope
/// for this benchmark walkthrough.
/// </summary>
/// <remarks>
/// No Azure OpenAI credentials required — this sample is metadata-only.
/// </remarks>
public static class LongMemEvalBenchmarkSample
{
    public static Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B8: LongMemEval — Memory benchmark (ICLR 2025)",
            "Subset (30Q embedded) vs Full (~500Q, requires LONGMEMEVAL_DATASET_PATH)");

        // Touch the AgentEval.Memory assembly so its [ModuleInitializer] fires before
        // we enumerate the registry. The discard suppresses an unused-variable warning.
        _ = typeof(MemoryBenchmark);

        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        if (family is null)
        {
            Console.WriteLine("   LongMemEval family is not registered. Did the umbrella package include AgentEval.Memory?");
            return Task.CompletedTask;
        }

        Console.WriteLine($"   Family:      {family.Name}");
        Console.WriteLine($"   Description: {family.Description}");
        Console.WriteLine($"   Cost tier:   {family.DefaultCostTier}");
        if (!string.IsNullOrEmpty(family.DocLinkUrl))
            Console.WriteLine($"   Docs:        {family.DocLinkUrl}");
        Console.WriteLine();
        Console.WriteLine($"   Presets ({family.Presets.Count}):");
        foreach (var p in family.Presets)
        {
            var tier = p.CostTier?.ToString() ?? family.DefaultCostTier.ToString();
            Console.WriteLine($"     - {p.Name,-12} [{tier,-6}] {p.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("   USAGE PATTERN (Shape B — runner-style):");
        Console.WriteLine("     var runner = LongMemEvalBenchmark.Subset(chatClient);");
        Console.WriteLine("     var result = await runner.RunAsync(agent, config, LongMemEvalBenchmark.SubsetOptions);");
        Console.WriteLine();
        Console.WriteLine("   For the full dataset:");
        Console.WriteLine("     1. Download from the official LongMemEval repo (ICLR 2025).");
        Console.WriteLine("     2. Set LONGMEMEVAL_DATASET_PATH to the JSON path.");
        Console.WriteLine("     3. Use LongMemEvalBenchmark.Full(chatClient) — it now throws when the env var is unset.");
        Console.WriteLine();
        Console.WriteLine("   See samples/AgentEval.Samples/MemoryEvaluation/ for end-to-end memory-agent flows.");
        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - LongMemEval is a Shape B family — runner returns a runner-style object, not a CompositeEval.");
        Console.WriteLine("   - Subset is bundled (30 questions) for CI; Full requires the operator-supplied dataset.");
        Console.WriteLine("   - v0.10.0 hardened Full() to throw on missing dataset rather than silently degrading.");

        return Task.CompletedTask;
    }
}
