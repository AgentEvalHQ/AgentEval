using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Interface for evaluating memory persistence across session boundaries.
/// Tests whether an agent retains facts after session resets.
/// </summary>
public interface ICrossSessionEvaluator
{
    /// <summary>
    /// Evaluates cross-session memory by planting facts, resetting the session,
    /// then testing whether facts are still recalled.
    /// </summary>
    /// <param name="agent">The agent to test (should implement ISessionResettableAgent)</param>
    /// <param name="facts">Facts to plant before the session reset</param>
    /// <param name="successThreshold">Minimum proportion of facts that must survive (0.0-1.0, default 0.8)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cross-session evaluation result</returns>
    Task<CrossSessionResult> EvaluateAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryFact> facts,
        double successThreshold = 0.8,
        CancellationToken cancellationToken = default);
}
