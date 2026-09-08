// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Evaluation harness for running agent evaluations.
/// </summary>
public interface IEvaluationHarness
{
    /// <summary>
    /// Run a single evaluation case against an agent.
    /// </summary>
    /// <param name="agent">The agent to evaluate.</param>
    /// <param name="testCase">The test case to run.</param>
    /// <param name="options">Optional evaluation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result.</returns>
    Task<TestResult> RunEvaluationAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended harness that supports streaming evaluations.
/// </summary>
public interface IStreamingEvaluationHarness : IEvaluationHarness
{
    /// <summary>
    /// Run an evaluation with streaming for detailed timing metrics.
    /// </summary>
    Task<TestResult> RunEvaluationStreamingAsync(
        IStreamableAgent agent,
        TestCase testCase,
        StreamingOptions? streamingOptions = null,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended harness that supports running a batch of dataset test cases.
/// </summary>
/// <remarks>
/// Converts <see cref="DatasetTestCase"/> to <see cref="TestCase"/> using
/// <see cref="DatasetTestCaseExtensions.ToTestCase"/> and aggregates results into a <see cref="TestSummary"/>.
/// </remarks>
public interface IBatchEvaluationHarness : IEvaluationHarness
{
    /// <summary>
    /// Run evaluation for all test cases in a dataset and aggregate results.
    /// </summary>
    /// <param name="agent">The agent to evaluate.</param>
    /// <param name="testCases">Dataset test cases to run.</param>
    /// <param name="options">Optional evaluation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="TestSummary"/> with <see cref="TestSummary.TotalCount"/> and <see cref="TestSummary.PassedCount"/>.</returns>
    Task<TestSummary> RunBatchAsync(
        IEvaluableAgent agent,
        IEnumerable<DatasetTestCase> testCases,
        EvaluationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for evaluation execution.
/// </summary>
public class EvaluationOptions
{
    /// <summary>Whether to track tool/function calls.</summary>
    public bool TrackTools { get; init; } = true;

    /// <summary>
    /// Whether the tool-usage report records <b>approval-gated</b> tool calls (MAF's
    /// <c>ToolApprovalRequestContent</c>, which wraps the call the agent emitted). Default
    /// <see langword="false"/>: such calls are dropped, counted in
    /// <c>ToolUsageReport.DroppedApprovalRequestCount</c>, and the harness logs a warning naming them.
    /// </summary>
    /// <remarks>
    /// Opt-in on purpose (ADR-030 Slice 0.5). Turning it on is the one change that can move a verdict —
    /// <c>NeverCallTool</c> on a gated tool can now fail, <c>MustCallTool</c> can now pass — so it must not
    /// happen silently under anyone's existing numbers. A gated call is recorded with
    /// <c>WasExecuted == false</c> and <c>ApprovalState == "Requested"</c>; an approval request is not an
    /// execution.
    /// </remarks>
    public bool IncludeApprovalGatedToolCalls { get; init; }

    /// <summary>Whether to track performance metrics.</summary>
    public bool TrackPerformance { get; init; } = true;
    
    /// <summary>Whether to evaluate the response with AI.</summary>
    public bool EvaluateResponse { get; init; } = true;
    
    /// <summary>Whether to print verbose output.</summary>
    public bool Verbose { get; init; } = true;
    
    /// <summary>Model name for cost estimation.</summary>
    public string? ModelName { get; init; }

    /// <summary>
    /// Optional list of metric names to run. When set, only these metrics are evaluated.
    /// When null or empty, all applicable metrics run (default behavior).
    /// </summary>
    /// <remarks>
    /// Metric names are case-insensitive. Use the naming convention prefixes:
    /// <c>llm_</c> (LLM-evaluated), <c>code_</c> (code-computed), <c>embed_</c> (embedding-based).
    /// Example: <c>["llm_relevance", "code_tool_success"]</c>.
    /// </remarks>
    public IReadOnlyList<string>? SelectedMetrics { get; set; }

    /// <summary>
    /// Optional Glass Box trace (an <c>AgentEval.Tracing.AgentTrace</c>) whose <c>ToolExecution</c> entries carry
    /// real per-tool execution timing. When set, the MAF harness back-fills that timing onto the extracted
    /// tool-usage report so the duration assertions (<c>WithDurationUnder</c> / <c>HaveAverageToolTimeUnder</c> /
    /// <c>HaveTotalToolTimeUnder</c>) evaluate instead of silently skipping.
    /// <para>
    /// Typed as <see cref="object"/> because this Abstractions assembly cannot reference the Core trace type;
    /// the harness casts it back. Ignored when null or not an <c>AgentTrace</c>.
    /// </para>
    /// </summary>
    public object? GlassBoxTrace { get; set; }
}

/// <summary>
/// Options for streaming evaluation execution.
/// </summary>
public class StreamingOptions
{
    /// <summary>Callback invoked for each text chunk received.</summary>
    public Action<string>? OnTextChunk { get; init; }
    
    /// <summary>Callback invoked when a tool starts executing.</summary>
    public Action<ToolCallRecord>? OnToolStart { get; init; }
    
    /// <summary>Callback invoked when a tool completes.</summary>
    public Action<ToolCallRecord>? OnToolComplete { get; init; }
    
    /// <summary>Callback invoked when first token is received.</summary>
    public Action<TimeSpan>? OnFirstToken { get; init; }
    
    /// <summary>Callback invoked periodically with updated metrics.</summary>
    public Action<PerformanceMetrics>? OnMetricsUpdate { get; init; }
}
