// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// Interface for comparing multiple AI models (agents) on the same test cases.
/// Enables head-to-head performance comparisons with statistical analysis.
/// </summary>
/// <remarks>
/// Model comparison runs stochastic tests on multiple models to compare:
/// - Quality (average scores)
/// - Speed (latency distributions)
/// - Cost (token usage and estimated costs)
/// - Reliability (pass rates and consistency)
/// 
/// Results are ranked using configurable weights for each dimension,
/// allowing users to optimize for different priorities (e.g., quality vs. cost).
/// Implementations should support concurrent model testing for efficiency.
/// </remarks>
public interface IModelComparer
{
    /// <summary>
    /// Compares multiple models on a single test case with stochastic analysis.
    /// Each model is run multiple times and results are aggregated and ranked.
    /// </summary>
    /// <param name="factories">
    /// Agent factories for each model to compare. Must contain at least one factory.
    /// Each factory should be configured for a different model/configuration.
    /// </param>
    /// <param name="testCase">The test case to run on all models. Cannot be null.</param>
    /// <param name="options">
    /// Comparison options including number of runs per model, scoring weights,
    /// statistical analysis settings, etc. If null, uses <see cref="ModelComparisonOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the comparison operation.</param>
    /// <returns>
    /// Comparison result containing per-model statistics, rankings, and a winner
    /// determined by the composite score based on configured weights.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factories or testCase is null.</exception>
    /// <exception cref="ArgumentException">Thrown when factories collection is empty.</exception>
    Task<ModelComparisonResult> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        TestCase testCase,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Compares multiple models across multiple test cases.
    /// This is a convenience method that runs <see cref="CompareModelsAsync(IReadOnlyList{IAgentFactory}, TestCase, ModelComparisonOptions?, CancellationToken)"/>
    /// for each test case sequentially.
    /// </summary>
    /// <param name="factories">
    /// Agent factories for each model to compare. Must contain at least one factory.
    /// Each factory should be configured for a different model/configuration.
    /// </param>
    /// <param name="testCases">The test cases to run. Cannot be null or empty.</param>
    /// <param name="options">
    /// Comparison options applied to all test cases. If null, uses <see cref="ModelComparisonOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the comparison operation.</param>
    /// <returns>
    /// List of comparison results, one for each test case, in the same order as testCases.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factories or testCases is null.</exception>
    /// <exception cref="ArgumentException">Thrown when factories or testCases collection is empty.</exception>
    Task<IReadOnlyList<ModelComparisonResult>> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        IReadOnlyList<TestCase> testCases,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default);
}
