// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Result of a cross-session memory evaluation, measuring fact persistence across session resets.
/// </summary>
public class CrossSessionResult
{
    /// <summary>
    /// Name of the cross-session scenario that was evaluated.
    /// </summary>
    public required string ScenarioName { get; init; }

    /// <summary>
    /// Whether the evaluation passed overall.
    /// </summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// Overall score (0-100) based on the proportion of facts that survived.
    /// </summary>
    public required double OverallScore { get; init; }

    /// <summary>
    /// Individual results for each fact tested across sessions.
    /// </summary>
    public required IReadOnlyList<CrossSessionFactResult> FactResults { get; init; }

    /// <summary>
    /// Whether the agent supports session reset (ISessionResettableAgent).
    /// </summary>
    public required bool SessionResetSupported { get; init; }

    /// <summary>
    /// Number of session resets performed during the evaluation.
    /// </summary>
    public int SessionResetCount { get; init; }

    /// <summary>
    /// Optional error message if the evaluation could not complete.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Total execution time for the cross-session evaluation.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of facts retained across sessions.
    /// </summary>
    public int RetainedCount => FactResults.Count(f => f.Recalled);

    /// <summary>
    /// Number of facts lost across sessions.
    /// </summary>
    public int LostCount => FactResults.Count(f => !f.Recalled);
}

/// <summary>
/// Result for a single fact tested across session boundaries.
/// </summary>
public class CrossSessionFactResult
{
    /// <summary>
    /// The fact that was tested.
    /// </summary>
    public required string Fact { get; init; }

    /// <summary>
    /// The query used to test recall.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// The agent's response to the query in the new session.
    /// </summary>
    public required string Response { get; init; }

    /// <summary>
    /// Whether the fact was recalled in the new session.
    /// </summary>
    public required bool Recalled { get; init; }

    /// <summary>
    /// Score (0-100) for this fact's recall.
    /// </summary>
    public required double Score { get; init; }
}
