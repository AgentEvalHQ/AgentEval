// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Pins that the four benchmark factory types visible to this project live in
/// <c>AgentEval.Benchmarks</c>.  A companion test in <c>AgentEval.Memory.Tests</c>
/// covers <c>MemoryBenchmark</c>, which is in a separate assembly reference.
/// </summary>
public class BenchmarkNamespaceContractTests
{
    private const string ExpectedNamespace = "AgentEval.Benchmarks";

    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.PerformanceBenchmark))]
    public void BenchmarkType_LivesIn_AgentEvalBenchmarksNamespace(Type benchmarkType)
    {
        Assert.Equal(ExpectedNamespace, benchmarkType.Namespace);
    }

    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.PerformanceBenchmark))]
    public void BenchmarkType_IsPublic(Type benchmarkType)
    {
        Assert.True(benchmarkType.IsPublic, $"{benchmarkType.FullName} must be public");
    }

    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    public void StaticBenchmarkFactory_IsAbstractSealed(Type factoryType)
    {
        // C# static classes compile to abstract+sealed in IL.
        Assert.True(factoryType.IsAbstract && factoryType.IsSealed,
            $"{factoryType.Name} must be a static class (abstract+sealed in IL)");
    }

    [Fact]
    public void NoOrphanBenchmarkFactoryInOldNamespaces()
    {
        // Guard: types must no longer exist in their original namespaces after Phase 4 migration.
        var forbiddenQualifiedNames = new[]
        {
            "AgentEval.Evals.Agentic.AgenticBenchmark",
            "AgentEval.GdprBenchmark.GdprBenchmark",
            "AgentEval.EuAiActBenchmark.EuAiActBenchmark",
        };

        // Force the target assemblies to load
        _ = typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.EuAiActBenchmark).Assembly;

        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .ToList();

        foreach (var fqn in forbiddenQualifiedNames)
        {
            var found = loadedAssemblies
                .Select(a => a.GetType(fqn, throwOnError: false))
                .Any(t => t is not null);

            Assert.False(found,
                $"Type '{fqn}' must no longer exist; it was found in a loaded assembly, " +
                "indicating the namespace migration was not completed.");
        }
    }
}
