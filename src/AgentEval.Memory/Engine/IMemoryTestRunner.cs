// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Engine;

/// <summary>
/// Interface for running memory evaluation tests against AI agents.
/// Orchestrates the complete memory testing pipeline.
/// </summary>
public interface IMemoryTestRunner
{
    /// <summary>
    /// Runs a complete memory evaluation scenario against an agent.
    /// </summary>
    /// <param name="agent">The agent to evaluate</param>
    /// <param name="scenario">The memory test scenario to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive memory evaluation results</returns>
    Task<MemoryEvaluationResult> RunAsync(
        IEvaluableAgent agent, 
        MemoryTestScenario scenario, 
        CancellationToken cancellationToken = default);
        
    /// <summary>
    /// Runs a memory test scenario against an agent and evaluates the results.
    /// </summary>
    /// <param name="agent">The agent to test memory capabilities</param>
    /// <param name="scenario">The memory test scenario to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The evaluation result with memory performance metrics</returns>
    Task<MemoryEvaluationResult> RunMemoryTestAsync(
        IEvaluableAgent agent, 
        MemoryTestScenario scenario, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Runs multiple memory queries against an agent and evaluates collective performance.
    /// </summary>
    /// <param name="agent">The agent to test memory capabilities</param>
    /// <param name="queries">The memory queries to execute</param>
    /// <param name="scenarioName">Name/description of the test scenario</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The evaluation result with memory performance metrics</returns>
    Task<MemoryEvaluationResult> RunMemoryQueriesAsync(
        IEvaluableAgent agent, 
        IEnumerable<MemoryQuery> queries, 
        string scenarioName,
        CancellationToken cancellationToken = default);
}