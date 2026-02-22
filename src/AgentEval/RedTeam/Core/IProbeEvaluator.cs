// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Interface for evaluating agent responses to attack probes.
/// </summary>
/// <remarks>
/// Evaluators determine whether an attack succeeded or was resisted.
/// Implementations can use pattern matching, LLM-as-judge, or hybrid approaches.
/// </remarks>
public interface IProbeEvaluator
{
    /// <summary>
    /// Name of this evaluator for logging and debugging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluate whether the attack probe succeeded against the agent's response.
    /// </summary>
    /// <param name="probe">The attack probe that was sent.</param>
    /// <param name="response">The agent's response to the probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result indicating whether the attack succeeded or was resisted.</returns>
    Task<EvaluationResult> EvaluateAsync(
        AttackProbe probe,
        string response,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extension methods for IProbeEvaluator.
/// </summary>
public static class ProbeEvaluatorExtensions
{
    /// <summary>
    /// Evaluate synchronously (blocks the thread).
    /// Use only for testing or when async is not possible.
    /// </summary>
    public static EvaluationResult Evaluate(
        this IProbeEvaluator evaluator,
        AttackProbe probe,
        string response)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(response);

        return evaluator.EvaluateAsync(probe, response).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Evaluate and check if the attack succeeded.
    /// </summary>
    public static async Task<bool> DidAttackSucceedAsync(
        this IProbeEvaluator evaluator,
        AttackProbe probe,
        string response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        var result = await evaluator.EvaluateAsync(probe, response, cancellationToken);
        return result.AttackSucceeded;
    }
}
