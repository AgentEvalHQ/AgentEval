// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Interface for evaluating how far back an agent can recall facts through layers of noise.
/// Parametrically tests recall at increasing depths to find the degradation curve.
/// </summary>
public interface IReachBackEvaluator
{
    /// <summary>
    /// Evaluates recall at multiple noise depths and builds a degradation profile.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="fact">The fact to plant at the beginning</param>
    /// <param name="query">The query to test recall</param>
    /// <param name="depths">Noise depths to test (e.g., 5, 10, 25, 50, 100)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Reach-back evaluation result with degradation curve</returns>
    Task<ReachBackResult> EvaluateAsync(
        IEvaluableAgent agent,
        MemoryFact fact,
        MemoryQuery query,
        IReadOnlyList<int> depths,
        CancellationToken cancellationToken = default);
}
