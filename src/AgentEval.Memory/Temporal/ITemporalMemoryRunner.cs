using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Temporal;

/// <summary>
/// Interface for running temporal memory evaluation tests.
/// Handles time-travel queries and temporal reasoning scenarios.
/// </summary>
public interface ITemporalMemoryRunner
{
    /// <summary>
    /// Executes a temporal memory scenario with time-travel queries.
    /// </summary>
    /// <param name="agent">The agent to test temporal memory capabilities</param>
    /// <param name="scenario">The temporal memory scenario to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Temporal memory evaluation result</returns>
    Task<MemoryEvaluationResult> RunTemporalScenarioAsync(
        IEvaluableAgent agent,
        MemoryTestScenario scenario,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Tests time-travel query capabilities with specific temporal anchors.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="conversationHistory">Full conversation history</param>
    /// <param name="temporalQueries">Queries with specific time references</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Temporal query evaluation results</returns>
    Task<MemoryEvaluationResult> TestTimeTravelQueriesAsync(
        IEvaluableAgent agent,
        IEnumerable<string> conversationHistory,
        IEnumerable<MemoryQuery> temporalQueries,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Evaluates causal reasoning about temporal relationships between facts.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="causalChain">Sequence of causally related events/facts</param>
    /// <param name="reasoningQueries">Queries about causal relationships</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Causal reasoning evaluation results</returns>
    Task<MemoryEvaluationResult> TestCausalReasoningAsync(
        IEvaluableAgent agent,
        IEnumerable<MemoryFact> causalChain,
        IEnumerable<string> reasoningQueries,
        CancellationToken cancellationToken = default);
}