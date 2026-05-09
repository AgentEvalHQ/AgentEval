// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures the worst-case per-tool mean latency against a configurable
/// per-tool budget.
/// <para>
/// <b>Score formula</b>: uses the tool with the highest mean latency as the driving value.
/// <list type="bullet">
///   <item>1.0 if max tool latency &lt;= <c>perToolBudgetMs</c></item>
///   <item>0.0 if max tool latency &gt; 2 × <c>perToolBudgetMs</c></item>
///   <item>Linear interpolation between budget and 2× budget.</item>
/// </list>
/// If <see cref="AgenticTelemetry.ToolLatencyMs"/> is empty, the evaluator returns score 1.0
/// (no per-tool data — no violation).
/// </para>
/// <para>
/// <b>Severity</b>: <c>low</c> — tool latency issues degrade user experience but are usually
/// addressable at the infrastructure level.
/// </para>
/// <para>
/// <b>Input contract</b>: telemetry data must be provided either via the constructor or via
/// <see cref="EvalInput.Metadata"/> under key <c>"agentic_telemetry"</c>
/// (an <see cref="AgenticTelemetry"/> instance). Per-input metadata takes precedence.
/// </para>
/// </summary>
public sealed class ToolLatencyEval : IEval
{
    private const string KeyValue      = "tool_latency";
    private const string NameValue     = "Tool Latency";
    private const string CategoryValue = "operational";
    private const string VersionValue  = "1.0.0";

    private readonly AgenticTelemetry? _data;
    private readonly double _perToolBudgetMs;

    /// <inheritdoc/>
    public string Key      => KeyValue;

    /// <inheritdoc/>
    public string Name     => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version  => VersionValue;

    /// <summary>
    /// Initialises a <see cref="ToolLatencyEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="perToolBudgetMs">
    /// Maximum acceptable mean latency per tool in milliseconds. Default: 2,000 ms.
    /// </param>
    public ToolLatencyEval(double perToolBudgetMs = 2000)
        : this(null, perToolBudgetMs) { }

    /// <summary>
    /// Initialises a <see cref="ToolLatencyEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="perToolBudgetMs">
    /// Maximum acceptable mean latency per tool in milliseconds. Default: 2,000 ms.
    /// </param>
    public ToolLatencyEval(AgenticTelemetry? data, double perToolBudgetMs = 2000)
    {
        _data            = data;
        _perToolBudgetMs = perToolBudgetMs;
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var telemetry = TelemetryHelper.Resolve(input, _data);
        if (telemetry is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"ToolLatencyEval requires telemetry data via EvalInput.Metadata[\"{LatencyEval.MetadataKey}\"] or constructor parameter."));
        }

        if (telemetry.ToolLatencyMs.Count == 0)
        {
            // No per-tool data — no violation.
            return Task.FromResult(new EvalResult(
                Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
                Score: new(1.0, null, "pass", true, null, "none", null),
                Details: new(
                    Dimensions: new Dictionary<string, double> { ["tool_count"] = 0 },
                    Evidence:
                    [
                        new EvalEvidence("telemetry", "tool_latency",
                            "No per-tool latency data available — score defaulted to 1.0."),
                    ],
                    Recommendations: null,
                    SubResults: null,
                    AggregationStrategy: null),
                Provenance: new("atomic-code", null, null, null, null, 0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
        }

        // Find the worst (highest-latency) tool.
        var (worstTool, worstLatency) = telemetry.ToolLatencyMs
            .OrderByDescending(kv => kv.Value)
            .First();

        var score = TelemetryHelper.LinearScore(worstLatency, _perToolBudgetMs);
        const double passThreshold = 0.80;
        var passed = score >= passThreshold;
        var severity = passed ? "none" : "low";

        // Build per-tool dimensions.
        var dimensions = new Dictionary<string, double>
        {
            ["worst_tool_latency_ms"] = worstLatency,
            ["per_tool_budget_ms"]    = _perToolBudgetMs,
            ["tool_count"]            = telemetry.ToolLatencyMs.Count,
        };
        foreach (var (name, ms) in telemetry.ToolLatencyMs)
            dimensions[$"tool_{name}_ms"] = ms;

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, passThreshold, severity, null),
            Details: new(
                Dimensions: dimensions,
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "tool_latency",
                        Message: $"Worst tool: '{worstTool}' at {worstLatency:F0} ms mean latency " +
                                 $"vs budget {_perToolBudgetMs:F0} ms."),
                ],
                Recommendations: passed ? null :
                [
                    $"Tool '{worstTool}' mean latency ({worstLatency:F0} ms) exceeds the per-tool budget " +
                    $"({_perToolBudgetMs:F0} ms). Review tool implementation, upstream service SLAs, or add caching."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
