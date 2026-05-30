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
/// <remarks>
/// <paramref name="Skipped"/> is optional (default 0) for backward compatibility with
/// previously-written summaries. Skipped leaves (Label "skipped") must NOT be counted as
/// failures (BUG-04); they get their own bucket so the counts reconcile against
/// <paramref name="Total"/>.
/// </remarks>
public sealed record RunStats(int Total, int Passed, int Failed, int Warnings, int Skipped = 0);

/// <summary>Estimated cost and token usage for a run.</summary>
public sealed record RunCostInfo(double EstimatedCost, long PromptTokens, long CompletionTokens);
