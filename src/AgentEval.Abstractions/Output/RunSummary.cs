// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Schema-mirroring record for a per-run <c>summary.json</c> file.</summary>
public sealed record RunSummary(
    string SchemaVersion,
    string RunId,
    string Verdict,
    RunStats Stats,
    IReadOnlyDictionary<string, double> Metrics,
    RunCostInfo? Cost = null);

/// <summary>Aggregated scenario counts for a run.</summary>
public sealed record RunStats(int Total, int Passed, int Failed, int Warnings);

/// <summary>Estimated cost and token usage for a run.</summary>
public sealed record RunCostInfo(double EstimatedCost, long PromptTokens, long CompletionTokens);
