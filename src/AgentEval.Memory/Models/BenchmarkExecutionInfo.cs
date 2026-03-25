// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Metadata about the benchmark execution itself (not the agent, but the run).
/// </summary>
public class BenchmarkExecutionInfo
{
    /// <summary>Preset name ("Quick", "Standard", "Full").</summary>
    public required string Preset { get; init; }

    /// <summary>Total execution time for the benchmark run.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Total number of LLM calls made during the benchmark.</summary>
    public int? TotalLlmCalls { get; init; }

    /// <summary>Estimated cost in USD for the benchmark run.</summary>
    public double? EstimatedCostUsd { get; init; }

    /// <summary>Scenario depth level used ("Quick", "Standard", "Full").</summary>
    public string? ScenarioDepth { get; init; }

    /// <summary>
    /// Benchmark source: "native" for AgentEval benchmarks, "longmemeval" for LongMemEval, etc.
    /// Report uses this to select rendering logic. Default: "native".
    /// </summary>
    public string BenchmarkSource { get; init; } = "native";
}
