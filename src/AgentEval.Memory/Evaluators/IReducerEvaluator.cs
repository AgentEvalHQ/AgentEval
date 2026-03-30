// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Interface for evaluating information loss caused by chat context reduction/compression.
/// Measures what the reducer destroys, not just what it keeps.
/// </summary>
public interface IReducerEvaluator
{
    /// <summary>
    /// Evaluates reducer fidelity by planting facts in a conversation, letting the reducer
    /// compress the history, then testing whether the agent still recalls the facts.
    /// </summary>
    /// <param name="agent">The agent under test (with reducer configured)</param>
    /// <param name="facts">Key facts to plant before reduction</param>
    /// <param name="noiseCount">Number of noise turns to add before triggering reduction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Reducer evaluation result with fidelity scores</returns>
    Task<ReducerEvaluationResult> EvaluateAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryFact> facts,
        int noiseCount = 20,
        CancellationToken cancellationToken = default);
}
