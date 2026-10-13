// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>Progress update emitted after each benchmark category completes.</summary>
public sealed record BenchmarkProgress
{
    /// <summary>The completed category, when supplied by the runner, for retaining observed results on interruption.</summary>
    public BenchmarkCategoryResult? Result { get; init; }
    public required string CategoryName { get; init; }
    public required double Score { get; init; }
    public required bool Skipped { get; init; }
    public required int CompletedCategories { get; init; }
    public required int TotalCategories { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required TimeSpan EstimatedRemaining { get; init; }
}
