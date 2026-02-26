// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// Interface for running stochastic (repeated, randomized) evaluations on agents.
/// Enables statistical analysis of agent behavior across multiple runs.
/// </summary>
/// <remarks>
/// stochastic evaluation runs the same test case multiple times to:
/// - Measure reliability and consistency
/// - Identify flaky behaviors
/// - Calculate statistical confidence in results
/// - Compare performance distributions across runs
/// 
/// Implementations should support concurrent evaluation execution for performance
/// and provide detailed statistics including mean, median, confidence intervals,
/// and pass rate analysis.
/// </remarks>
public interface IStochasticRunner
{
    /// <summary>
    /// Runs a test case multiple times against the same agent instance.
    /// Use this method when evaluating stateful agents or when you want to reuse
    /// the same agent instance across all runs.
    /// </summary>
    /// <param name="agent">The agent to evaluate. Cannot be null.</param>
    /// <param name="testCase">The test case to run repeatedly. Cannot be null.</param>
    /// <param name="options">
    /// stochastic evaluation options (number of runs, parallelism, success threshold, etc.).
    /// If null, uses <see cref="StochasticOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the stochastic evaluation run.</param>
    /// <returns>
    /// Stochastic evaluation result containing individual run results, aggregate statistics,
    /// and pass/fail determination based on the success rate threshold.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when agent or testCase is null.</exception>
    Task<StochasticResult> RunStochasticTestAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Runs a test case multiple times, creating a fresh agent for each run.
    /// Use this method when you need isolated, independent runs without state carryover.
    /// This is the recommended approach for most stochastic evaluation scenarios.
    /// </summary>
    /// <param name="factory">Factory to create fresh agent instances. Cannot be null.</param>
    /// <param name="testCase">The test case to run repeatedly. Cannot be null.</param>
    /// <param name="options">
    /// stochastic evaluation options (number of runs, parallelism, success threshold, etc.).
    /// If null, uses <see cref="StochasticOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the stochastic evaluation run.</param>
    /// <returns>
    /// Stochastic evaluation result containing individual run results, aggregate statistics,
    /// and pass/fail determination based on the success rate threshold.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factory or testCase is null.</exception>
    Task<StochasticResult> RunStochasticTestAsync(
        IAgentFactory factory,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default);
}
