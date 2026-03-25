// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.External;

/// <summary>
/// Runner for an external benchmark (LongMemEval, LocBench, etc.).
/// Each external benchmark implements this interface with its own data format,
/// execution strategy, and scoring system.
/// </summary>
public interface IExternalBenchmarkRunner
{
    /// <summary>Unique identifier for this benchmark (e.g., "longmemeval").</summary>
    string BenchmarkId { get; }

    /// <summary>Display name (e.g., "LongMemEval (ICLR 2025)").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Runs the external benchmark against an agent.
    /// </summary>
    /// <param name="agent">The agent to benchmark.</param>
    /// <param name="config">Agent configuration metadata for baseline storage.</param>
    /// <param name="options">Benchmark-specific options (sampling, scoring mode, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Benchmark-native result that can be converted to a <see cref="MemoryBaseline"/>.</returns>
    Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        AgentBenchmarkConfig config,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default);
}
