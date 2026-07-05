// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals.Agentic.Diagnostics;

/// <summary>
/// Glass Box Phase 3 (A3) — pure-code evaluator over a Glass Box trace that measures per-tool reliability
/// from the real tool-execution boundary (<c>EvaluatingAIFunction</c>).
/// <para>
/// <b>Score</b>: the <b>minimum</b> per-tool success rate across all tools that executed (succeeded / total
/// per tool name), so one flaky tool drags the whole score down. Clamped to [0, 1].
/// </para>
/// <para><b>Pass threshold</b>: 0.90. <b>Severity</b> when failing: <c>high</c> (a tool that fails often is a
/// reliability/SLO problem that cascades).</para>
/// <para><b>Input</b>: reads <see cref="TraceEntryScope.ToolExecution"/> entries from
/// <c>EvalInput.GetTrace()</c>. Skipped when no trace is attached or the trace has no tool-execution entries.</para>
/// </summary>
public sealed class ToolReliabilityEval : IEval
{
    private const string KeyValue = "tool_reliability";
    private const string NameValue = "Tool Reliability";
    private const string CategoryValue = "agentic-process";
    private const string VersionValue = "1.0.0";
    private const double PassThreshold = 0.90;

    /// <inheritdoc/>
    public string Key => KeyValue;

    /// <inheritdoc/>
    public string Name => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version => VersionValue;

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        AgentTrace? trace = input.GetTrace();
        if (trace is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "ToolReliabilityEval requires a Glass Box trace attached via EvalInput.WithTrace(...)."));
        }

        // Per-tool (succeeded, total) over ToolExecution entries. ToolCalls is nullable — guard before [0].
        // Tool names are matched case-insensitively (matching ToolUsageReport.GetCallsByName) so mixed casing
        // is not counted as two different tools.
        var byTool = new Dictionary<string, (int Succeeded, int Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in trace.Entries)
        {
            if (entry.EffectiveScope != TraceEntryScope.ToolExecution)
                continue;
            if (entry.ToolCalls is not { Count: > 0 })
                continue;

            var call = entry.ToolCalls[0];
            var name = call.Name ?? "(unnamed)";
            var (succeeded, total) = byTool.TryGetValue(name, out var acc) ? acc : (0, 0);
            byTool[name] = (succeeded + (call.Succeeded ? 1 : 0), total + 1);
        }

        if (byTool.Count == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "ToolReliabilityEval: the trace has no ToolExecution entries to score."));
        }

        var perToolRates = byTool.ToDictionary(
            kv => kv.Key,
            kv => (double)kv.Value.Succeeded / kv.Value.Total);
        var score = Math.Clamp(perToolRates.Values.Min(), 0.0, 1.0);
        var passed = score >= PassThreshold;
        var severity = passed ? "none" : "high";

        var worst = perToolRates.OrderBy(kv => kv.Value).First();
        var dimensions = new Dictionary<string, double>
        {
            ["min_success_rate"] = score,
            ["tool_count"] = byTool.Count,
            ["total_executions"] = byTool.Values.Sum(v => v.Total),
        };

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, PassThreshold, severity, null),
            Details: new(
                Dimensions: dimensions,
                Evidence:
                [
                    new EvalEvidence(
                        Source: "tool-execution",
                        Reference: "tool_reliability",
                        Message: $"lowest per-tool success rate: '{worst.Key}' at {worst.Value:P0} "
                            + $"across {byTool.Count} tool(s), {byTool.Values.Sum(v => v.Total)} execution(s)."),
                ],
                Recommendations: passed ? null :
                [
                    $"Tool '{worst.Key}' succeeds only {worst.Value:P0} of the time (below {PassThreshold:P0}). "
                    + "Investigate flaky/failing calls; consider retry budgets or a fallback tool.",
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
