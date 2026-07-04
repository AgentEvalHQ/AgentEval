// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Pins that every benchmark factory type that ships in AgentEval lives in the
/// unified <c>AgentEval.Benchmarks</c> namespace. A companion test in
/// <c>AgentEval.Memory.Tests</c> covers <c>MemoryBenchmark</c>, which is in a
/// separate assembly reference.
/// </summary>
/// <remarks>
/// <para>
/// Phase 5 (v0.10.0-beta) refactor: the negative test now uses a reflection
/// enumerator across all loaded benchmark-bearing assemblies rather than a
/// hardcoded forbidden-FQN list. A future regression (a new factory class
/// added in the wrong namespace) will fail
/// <see cref="All_BenchmarkSuffixedFactories_LiveIn_AgentEvalBenchmarks"/>.
/// </para>
/// <para>
/// Documented exceptions to the "ends-in-Benchmark → must be in AgentEval.Benchmarks"
/// convention are listed in <see cref="DomainTypeExceptions"/>:
/// <c>*BenchmarkRunner</c> / <c>*BenchmarkResult</c> / <c>*BenchmarkReporter</c> /
/// <c>*BenchmarkOptions</c> / <c>*BenchmarkRun</c> / <c>*BenchmarkContext</c> are
/// infrastructure types and legitimately live in their domain namespaces.
/// </para>
/// </remarks>
public class BenchmarkNamespaceContractTests
{
    private const string ExpectedNamespace = "AgentEval.Benchmarks";

    // Static factory types (currently 6 — Memory is covered in AgentEval.Memory.Tests).
    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.PerformanceBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.OwaspBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.MitreBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.TraceFidelityBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.WorkflowTraceFidelityBenchmark))]
    public void BenchmarkType_LivesIn_AgentEvalBenchmarksNamespace(Type benchmarkType)
    {
        Assert.Equal(ExpectedNamespace, benchmarkType.Namespace);
    }

    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.PerformanceBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.OwaspBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.MitreBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.TraceFidelityBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.WorkflowTraceFidelityBenchmark))]
    public void BenchmarkType_IsPublic(Type benchmarkType)
    {
        Assert.True(benchmarkType.IsPublic, $"{benchmarkType.FullName} must be public");
    }

    /// <summary>
    /// Static-factory benchmarks must be abstract+sealed in IL (i.e. <c>public static</c> in C#).
    /// Instance benchmarks like <c>PerformanceBenchmark</c> are exempt because they hold mutable
    /// state (e.g. the wrapped <see cref="AgentEval.Core.IEvaluableAgent"/>).
    /// </summary>
    [Theory]
    [InlineData(typeof(AgentEval.Benchmarks.AgenticBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.GdprBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.EuAiActBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.OwaspBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.MitreBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.TraceFidelityBenchmark))]
    [InlineData(typeof(AgentEval.Benchmarks.WorkflowTraceFidelityBenchmark))]
    public void StaticBenchmarkFactory_IsAbstractSealed(Type factoryType)
    {
        // C# static classes compile to abstract+sealed in IL.
        Assert.True(factoryType.IsAbstract && factoryType.IsSealed,
            $"{factoryType.Name} must be a static class (abstract+sealed in IL)");
    }

    /// <summary>
    /// Reflection enumerator: walks every loaded benchmark-bearing assembly,
    /// enumerates types named <c>*Benchmark</c>, and asserts each is either in
    /// <c>AgentEval.Benchmarks</c> or appears in the documented exception list.
    /// </summary>
    /// <remarks>
    /// Replaces the v0.9.0/Phase-4 hardcoded forbidden-FQN list with a positive
    /// "every type ending in <c>Benchmark</c> must be in the unified namespace
    /// unless explicitly excepted" rule. A future regression — e.g. someone
    /// adding <c>MitreBenchmark</c> to <c>AgentEval.RedTeam.Compliance</c> instead
    /// of <c>AgentEval.Benchmarks</c> — will fail this test.
    /// </remarks>
    [Fact]
    public void All_BenchmarkSuffixedFactories_LiveIn_AgentEvalBenchmarks()
    {
        // Anchor: force every benchmark-bearing assembly to load by touching one
        // of its types. This way the enumerator below sees all candidates even
        // if no test has touched them yet.
        _ = typeof(AgentEval.Benchmarks.AgenticBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.EuAiActBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.PerformanceBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.OwaspBenchmark).Assembly;
        _ = typeof(AgentEval.Benchmarks.MitreBenchmark).Assembly;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic
                        && a.GetName().Name?.StartsWith("AgentEval", StringComparison.Ordinal) == true
                        // Test/sample assemblies have their own conventions — sticky:
                        && !a.GetName().Name!.Contains(".Tests", StringComparison.Ordinal)
                        && !a.GetName().Name!.Contains(".Samples", StringComparison.Ordinal)
                        && !a.GetName().Name!.Contains(".Demo", StringComparison.Ordinal)
                        && !a.GetName().Name!.Contains(".NuGetConsumer", StringComparison.Ordinal)
                        && !a.GetName().Name!.Contains(".TravelDemo", StringComparison.Ordinal))
            .ToList();

        var offenders = new List<string>();
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }

            foreach (var t in types)
            {
                if (!t.IsPublic) continue;
                if (!t.Name.EndsWith("Benchmark", StringComparison.Ordinal)) continue;
                if (DomainTypeExceptions.Contains(t.Name)) continue;
                if (t.Namespace == ExpectedNamespace) continue;

                offenders.Add($"{t.FullName} (in {asm.GetName().Name})");
            }
        }

        Assert.True(offenders.Count == 0,
            $"These benchmark-suffixed factory types are not in '{ExpectedNamespace}' and not in the " +
            $"documented exception list (see {nameof(DomainTypeExceptions)}): " +
            $"[{string.Join(", ", offenders)}]. " +
            "Either move the type to AgentEval.Benchmarks, or — if it is a domain-runner / -result / " +
            "-reporter / -options / -context type rather than a factory — add its short name to the " +
            "exception list with a one-line comment justifying the exception.");
    }

    /// <summary>
    /// Documented exceptions to the "ends-in-Benchmark → must be in AgentEval.Benchmarks" rule.
    /// These types are infrastructure / domain types that legitimately live in their domain
    /// namespaces; they are NOT factories. Each entry should be paired with a one-line
    /// justification comment.
    /// </summary>
    /// <remarks>
    /// Use short name (no namespace) so the same name in different assemblies is covered
    /// by one entry. If two unrelated types share a short name, switch to FQN.
    /// </remarks>
    private static readonly HashSet<string> DomainTypeExceptions = new(StringComparer.Ordinal)
    {
        // Reflectively look these up later if convenient; for now keep an explicit list.
        // Each line: short type name, then a comment justifying its exception.

        "AgenticBenchmarkRunner",         // runner: drives a CompositeEval through the output-store
        "AgenticBenchmarkReporter",       // reporter: produces Markdown + PDF reports
        "AgenticBenchmarkResult",         // result record: per-run output of AgenticBenchmarkRunner
        "AgenticBenchmarkOptions",        // configuration: thresholds / report shape
        "AgenticBenchmarkContext",        // run-time context passed into the runner

        "GdprBenchmarkRunner",            // GDPR runner
        "EuAiActBenchmarkRunner",         // EU AI Act runner

        "MemoryBenchmarkRunner",          // Memory runner (Memory assembly; covered by AgentEval.Memory.Tests)
        "MemoryBenchmarkResult",          // Memory result record
        "MemoryBenchmarkReport",          // Memory report
        "MemoryBenchmarkCategory",        // Memory category enum/record
        "MemoryBenchmarkOptions",         // Memory options

        "PerformanceBenchmarkOptions",    // perf options
        "PerformanceBenchmarkRun",        // perf run record
        "PerformanceBenchmarkEvaluateOptions", // perf evaluate-async options (Phase 3)

        // OWASP companions (Phase 5):
        "OwaspBenchmarkRun",              // the thin wrapper around AttackPipeline + reporter

        // MITRE ATLAS companions (Phase 6):
        "MitreBenchmarkRun",              // the thin wrapper around AttackPipeline + MITREATLASReporter

        // External academic benchmarks may add *BenchmarkRunner companions here later
        // (e.g. LongMemEvalBenchmarkRunner in Phase 7).
        "LongMemEvalBenchmarkRunner",
    };
}
