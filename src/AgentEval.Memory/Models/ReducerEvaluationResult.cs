// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Result of a chat reducer fidelity evaluation, measuring information loss during context compression.
/// </summary>
public class ReducerEvaluationResult
{
    /// <summary>
    /// Name of the evaluation scenario.
    /// </summary>
    public required string ScenarioName { get; init; }

    /// <summary>
    /// Individual results for each fact tested after reduction.
    /// </summary>
    public required IReadOnlyList<ReducerFactResult> FactResults { get; init; }

    /// <summary>
    /// Fidelity score (0-100): percentage of key facts retained after reduction.
    /// </summary>
    public double FidelityScore => MeasuredFacts > 0
        ? FactResults.Count(f => f.Retained) / (double)MeasuredFacts * 100
        : 0;

    /// <summary>Facts the judge scored. The others are left out of every count here and reported.</summary>
    public int MeasuredFacts => FactResults.Count(f => f.Measured);

    /// <summary>Facts the judge produced no score for.</summary>
    public int UnmeasuredFacts => FactResults.Count - MeasuredFacts;

    /// <summary>True when at least one fact was scored, so <see cref="FidelityScore"/> means something.</summary>
    public bool IsMeasured => MeasuredFacts > 0;

    /// <summary>
    /// Number of facts retained after reduction.
    /// </summary>
    public int RetainedCount => FactResults.Count(f => f.Retained);

    /// <summary>
    /// Number of facts lost during reduction.
    /// </summary>
    public int LostCount => FactResults.Count(f => f.Measured && !f.Retained);

    /// <summary>
    /// Whether any critical (high-importance) facts were lost.
    /// </summary>
    public bool HasCriticalLoss => FactResults.Any(f => f.Measured && !f.Retained && f.Fact.Importance >= 80);

    /// <summary>
    /// Facts that were lost during reduction, ordered by importance (highest first).
    /// </summary>
    public IReadOnlyList<MemoryFact> LostFacts => FactResults
        .Where(f => f.Measured && !f.Retained)
        .OrderByDescending(f => f.Fact.Importance)
        .Select(f => f.Fact)
        .ToList();

    /// <summary>
    /// Facts that were retained after reduction, ordered by importance (highest first).
    /// </summary>
    public IReadOnlyList<MemoryFact> RetainedFacts => FactResults
        .Where(f => f.Retained)
        .OrderByDescending(f => f.Fact.Importance)
        .Select(f => f.Fact)
        .ToList();

    /// <summary>
    /// Whether the evaluation passed (fidelity >= 80% and no critical losses).
    /// </summary>
    public bool Passed => UnmeasuredFacts == 0 && FidelityScore >= 80 && !HasCriticalLoss;

    /// <summary>
    /// Total execution time for the reducer evaluation.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of messages before reduction.
    /// </summary>
    public int PreReductionMessageCount { get; init; }

    /// <summary>
    /// Number of messages after reduction.
    /// </summary>
    public int PostReductionMessageCount { get; init; }
}

/// <summary>
/// Result describing whether a specific fact survived the reduction process.
/// </summary>
public class ReducerFactResult
{
    /// <summary>
    /// The fact that was tested.
    /// </summary>
    public required MemoryFact Fact { get; init; }

    /// <summary>
    /// Whether the fact was retained after reduction.
    /// </summary>
    public required bool Retained { get; init; }

    /// <summary>
    /// Score (0-100) for this fact's presence after reduction.
    /// </summary>
    public required double Score { get; init; }

    /// <summary><see langword="false"/> when the judge produced no score for this fact.</summary>
    public bool Measured { get; init; } = true;

    /// <summary>
    /// The agent's response when queried about this fact after reduction.
    /// </summary>
    public string? Response { get; init; }
}
