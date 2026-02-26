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
    
    /// <summary>Whether to track performance metrics.</summary>
    public bool TrackPerformance { get; init; } = true;
    
    /// <summary>Whether to evaluate the response with AI.</summary>
    public bool EvaluateResponse { get; init; } = true;
    
    /// <summary>Whether to print verbose output.</summary>
    public bool Verbose { get; init; } = true;
    
    /// <summary>Model name for cost estimation.</summary>
    public string? ModelName { get; init; }
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
