// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;

namespace AgentEval.Tests.Census;

/// <summary>
/// The product assemblies a census scans: one public type anchors each, so an assembly the test project stops
/// referencing fails to compile here instead of dropping out of every census in silence.
/// </summary>
/// <remarks>
/// MissionControl is left out: it is net10.0-only, so a census over it would differ by target framework. Its cost view
/// belongs to the cost task. The CLI is left out too: its producers are internal, and the aggregation census lists them
/// as rows it does not discover.
/// </remarks>
internal static class ProductAssemblies
{
    /// <summary>Every product assembly, in a stable order.</summary>
    internal static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(AgentEval.Evals.EvalScore).Assembly,                                    // Abstractions
        typeof(AgentEval.Evals.CompositeEval).Assembly,                                // Core
        typeof(AgentEval.Exporters.JsonExporter).Assembly,                             // DataLoaders
        typeof(AgentEval.Results.Runs.AefSummaryCalculator).Assembly,                  // Results
        typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter).Assembly,      // Results.Adapters
        typeof(AgentEval.MAF.MAFEvaluationHarness).Assembly,                           // MAF
        typeof(AgentEval.Memory.Models.MemoryBenchmarkResult).Assembly,                // Memory
        typeof(AgentEval.RedTeam.RedTeamResult).Assembly,                              // RedTeam
        typeof(AgentEval.RedTeam.Gatekeeper.CanaryToolGate).Assembly,                  // RedTeam.Gatekeeper
        typeof(AgentEval.Evals.Agentic.JudgeQuality.JudgeAgreementEval).Assembly,      // Evals.Agentic
        typeof(AgentEval.Benchmarks.PerformanceBenchmark).Assembly,                    // Evals.Performance
        typeof(AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner).Assembly,       // Compliance.Gdpr
        typeof(AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner).Assembly, // Compliance.EuAiAct
        typeof(AgentEval.Compliance.Core.CriticalFindingExtractor).Assembly,           // Compliance.Core
        typeof(AgentEval.Interop.AssertAi.AssertAiRun).Assembly,                       // Interop.AssertAi
    ];

    /// <summary>The exported types of every product assembly, outside <c>AgentEval.Testing</c> (test doubles).</summary>
    internal static IEnumerable<Type> ExportedTypes() =>
        All.SelectMany(a => a.GetExportedTypes())
            .Where(t => t.Namespace is not { } ns || !ns.StartsWith("AgentEval.Testing", StringComparison.Ordinal));
}
