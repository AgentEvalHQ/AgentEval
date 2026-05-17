// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Companion contract test to <c>BenchmarkNamespaceContractTests</c> in AgentEval.Tests.
/// Pins that <c>MemoryBenchmark</c> lives in <c>AgentEval.Benchmarks</c> after
/// the Phase-4 namespace consolidation.
/// </summary>
public class MemoryBenchmarkNamespaceContractTest
{
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
}
