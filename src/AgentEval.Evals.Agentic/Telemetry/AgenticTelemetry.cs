// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Per-run telemetry data passed to telemetry evaluators (latency, tokens, cost, errors,
/// retries, and per-tool latency).
/// <para>
/// Telemetry evaluators accept this record either via a constructor parameter or by reading
/// the key <c>"agentic_telemetry"</c> from <see cref="EvalInput.Metadata"/>. Per-input
/// metadata always takes precedence over the constructor-supplied value.
/// </para>
/// <para>
/// <b>Metadata contract</b>: to supply telemetry data through <see cref="EvalInput.Metadata"/>,
/// store an <see cref="AgenticTelemetry"/> instance under the key <c>"agentic_telemetry"</c>:
/// <code>
/// var input = new EvalInput(
///     Query: "...",
///     Metadata: new Dictionary&lt;string, object&gt;
///     {
///         ["agentic_telemetry"] = new AgenticTelemetry(
///             LatencyMsP50: 350, LatencyMsP95: 900, LatencyMsP99: 2000,
///             TokensUsed: 4200, EstimatedCostUsd: 0.08,
///             ErrorCount: 1, TotalCalls: 20, RetryCount: 2,
///             ToolLatencyMs: new Dictionary&lt;string, double&gt; { ["search"] = 450 })
///     });
/// </code>
/// </para>
/// </summary>
/// <param name="LatencyMsP50">End-to-end agent latency at the 50th percentile in milliseconds; null when not tracked, 0 for a measured zero.</param>
/// <param name="LatencyMsP95">End-to-end agent latency at the 95th percentile in milliseconds; null when not tracked, 0 for a measured zero.</param>
/// <param name="LatencyMsP99">End-to-end agent latency at the 99th percentile in milliseconds; null when not tracked, 0 for a measured zero. LatencyEval scores on this percentile alone.</param>
/// <param name="TokensUsed">Total tokens consumed (prompt + completion) across all LLM calls in the run; null when not tracked. A reported 0 is a measured zero and is within budget.</param>
/// <param name="EstimatedCostUsd">
/// Estimated monetary cost of the run in USD, or <see langword="null"/> when the run's cost was not tracked. Not tracked
/// is never 0: 0 is a measured zero (the run cost nothing), which <see cref="CostEval"/> scores as within any budget, so
/// a 0 written for "not tracked" passed every budget check (T82). A null cost is not measured by the cost evaluators.
/// </param>
/// <param name="ErrorCount">Number of calls that resulted in an error or exception.</param>
/// <param name="TotalCalls">Total number of calls (LLM + tool) made during the run.</param>
/// <param name="RetryCount">Total number of retries triggered by transient failures.</param>
/// <param name="ToolLatencyMs">Per-tool mean latency in milliseconds, keyed by tool name.</param>
public sealed record AgenticTelemetry(
    double? LatencyMsP50,
    double? LatencyMsP95,
    double? LatencyMsP99,
    int? TokensUsed,
    double? EstimatedCostUsd,
    int ErrorCount,
    int TotalCalls,
    int RetryCount,
    IReadOnlyDictionary<string, double> ToolLatencyMs);
