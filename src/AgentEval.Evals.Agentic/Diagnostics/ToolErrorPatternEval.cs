// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals.Agentic.Diagnostics;

/// <summary>
/// Glass Box Phase 3 (A3) — pure-code (deterministic) evaluator that surfaces concentrated tool-failure
/// patterns: many executions failing the same way, clustered by (tool name, normalized error text).
/// <para><b>Score</b>: <c>1 - largestErrorCluster / totalToolCalls</c>, where a cluster keys on
/// (tool name, normalized error text). <b>Threshold</b>: 0.80. <b>Severity</b> on fail: <c>medium</c>.</para>
/// <para>Reads ToolExecution entries; skipped when no trace or no tool-execution entries. A run with no
/// failures scores 1.0 (empty largest cluster).</para>
/// </summary>
public sealed class ToolErrorPatternEval : IEval
{
    private const string KeyValue = "tool_error_pattern";
    private const string NameValue = "Tool Error Pattern";
    private const string CategoryValue = "agentic-process";
    private const string VersionValue = "1.0.0";
    private const double PassThreshold = 0.80;

    /// <inheritdoc/>
    public string Key => KeyValue;

    /// <inheritdoc/>
    public string Name => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version => VersionValue;

    private static string Normalize(string? error) =>
        string.Join(' ', (error ?? "(no message)").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        AgentTrace? trace = input.GetTrace();
        if (trace is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "ToolErrorPatternEval requires a Glass Box trace attached via EvalInput.WithTrace(...)."));
        }

        var totalToolCalls = 0;
        var clusters = new Dictionary<(string Name, string Error), int>();
        foreach (var entry in trace.Entries)
        {
            if (entry.EffectiveScope != TraceEntryScope.ToolExecution)
                continue;
            if (entry.ToolCalls is not { Count: > 0 })
                continue;

            totalToolCalls++;
            var call = entry.ToolCalls[0];
            if (call.Succeeded)
                continue;

            var key = (call.Name ?? "(unnamed)", Normalize(call.Error));
            clusters[key] = clusters.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        if (totalToolCalls == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "ToolErrorPatternEval: the trace has no ToolExecution entries to score."));
        }

        var largestCluster = clusters.Count == 0 ? 0 : clusters.Values.Max();
        var score = Math.Clamp(1.0 - (double)largestCluster / totalToolCalls, 0.0, 1.0);
        var passed = score >= PassThreshold;

        var worst = clusters.Count == 0 ? default : clusters.OrderByDescending(kv => kv.Value).First().Key;
        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, PassThreshold, passed ? "none" : "medium", null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["total_tool_calls"] = totalToolCalls,
                    ["largest_error_cluster"] = largestCluster,
                    ["distinct_error_clusters"] = clusters.Count,
                },
                Evidence:
                [
                    new EvalEvidence("tool-execution", "tool_error_pattern",
                        largestCluster == 0
                            ? $"no tool failures across {totalToolCalls} execution(s)."
                            : $"'{worst.Name}' failed {largestCluster}x with the same error ('{worst.Error}') out of {totalToolCalls} call(s)."),
                ],
                Recommendations: passed ? null :
                [
                    $"A single failure mode accounts for {(double)largestCluster / totalToolCalls:P0} of tool calls. "
                    + "Fix the underlying cause (timeout / validation / availability) rather than retrying blindly.",
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
