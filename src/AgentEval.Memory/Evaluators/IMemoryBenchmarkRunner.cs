// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Interface for running comprehensive memory benchmark suites against agents.
/// Executes multiple scenarios across categories and produces a holistic memory quality score.
/// </summary>
public interface IMemoryBenchmarkRunner
{
    /// <summary>
    /// Runs a memory benchmark suite against an agent.
    /// </summary>
    /// <param name="agent">The agent to benchmark</param>
    /// <param name="benchmark">The benchmark preset to run (Quick, Standard, or Full)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive benchmark result with per-category scores</returns>
    Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a memory benchmark suite against an agent with optional progress reporting.
    /// </summary>
    /// <param name="agent">The agent to benchmark</param>
    /// <param name="benchmark">The benchmark preset to run (Quick, Standard, or Full)</param>
    /// <param name="progress">Optional progress reporter invoked after each category completes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive benchmark result with per-category scores</returns>
    Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        IProgress<BenchmarkProgress>? progress,
        CancellationToken cancellationToken = default);
}
