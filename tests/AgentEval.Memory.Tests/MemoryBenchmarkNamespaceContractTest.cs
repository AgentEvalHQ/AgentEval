// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Companion contract test to <c>BenchmarkNamespaceContractTests</c> in AgentEval.Tests.
/// Pins that Memory-assembly benchmark factory types live in <c>AgentEval.Benchmarks</c>.
/// Covers <c>MemoryBenchmark</c> (Phase 4) and <c>LongMemEvalBenchmark</c> (Phase 7).
/// </summary>
public class MemoryBenchmarkNamespaceContractTest
{
    // ─── MemoryBenchmark (Phase 4) ────────────────────────────────────────────

    [Fact]
    public void MemoryBenchmark_LivesIn_AgentEvalBenchmarksNamespace()
    {
        Assert.Equal("AgentEval.Benchmarks", typeof(AgentEval.Benchmarks.MemoryBenchmark).Namespace);
    }

    [Fact]
    public void MemoryBenchmark_IsPublic()
    {
        Assert.True(typeof(AgentEval.Benchmarks.MemoryBenchmark).IsPublic);
    }

    [Fact]
    public void NoOrphan_MemoryBenchmark_InOldNamespace()
    {
        // Force the assembly to load
        _ = typeof(AgentEval.Benchmarks.MemoryBenchmark).Assembly;

        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .ToList();

        var found = loadedAssemblies
            .Select(a => a.GetType("AgentEval.Memory.Models.MemoryBenchmark", throwOnError: false))
            .Any(t => t is not null);

        Assert.False(found,
            "AgentEval.Memory.Models.MemoryBenchmark must no longer exist after Phase-4 migration.");
    }

    // ─── LongMemEvalBenchmark (Phase 7) ───────────────────────────────────────

    [Fact]
    public void LongMemEvalBenchmark_LivesIn_AgentEvalBenchmarksNamespace()
    {
        // Convention 1 (ADR-017): benchmark factory types must live in AgentEval.Benchmarks.
        Assert.Equal("AgentEval.Benchmarks", typeof(AgentEval.Benchmarks.LongMemEvalBenchmark).Namespace);
    }

    [Fact]
    public void LongMemEvalBenchmark_IsPublic()
    {
        Assert.True(typeof(AgentEval.Benchmarks.LongMemEvalBenchmark).IsPublic,
            "LongMemEvalBenchmark must be public for API consumers.");
    }

    [Fact]
    public void LongMemEvalBenchmark_IsStaticClass_AbstractAndSealed()
    {
        var t = typeof(AgentEval.Benchmarks.LongMemEvalBenchmark);
        Assert.True(t.IsAbstract && t.IsSealed,
            $"{t.Name} must be a static class (abstract+sealed in IL); Convention 1 requires static partial.");
    }
}
