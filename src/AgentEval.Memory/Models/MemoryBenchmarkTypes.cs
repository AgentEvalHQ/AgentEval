// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// A category within a memory benchmark with a name, weight, and scenario type.
/// </summary>
public class MemoryBenchmarkCategory
{
    /// <summary>
    /// Display name for this category.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Weight of this category in the overall score (0.0-1.0).
    /// </summary>
    public required double Weight { get; init; }

    /// <summary>
    /// Type of scenario to run for this category.
    /// </summary>
    public required BenchmarkScenarioType ScenarioType { get; init; }
}

/// <summary>
/// Types of benchmark scenarios that can be executed.
/// </summary>
public enum BenchmarkScenarioType
{
    /// <summary>Tests basic fact retention over a short conversation.</summary>
    BasicRetention,
    /// <summary>Tests temporal reasoning with time-sensitive facts.</summary>
    TemporalReasoning,
    /// <summary>Tests recall through conversational noise.</summary>
    NoiseResilience,
    /// <summary>Tests recall depth through layers of noise.</summary>
    ReachBackDepth,
    /// <summary>Tests handling of updated/corrected facts.</summary>
    FactUpdateHandling,
    /// <summary>Tests memory across multiple conversation topics.</summary>
    MultiTopic,
    /// <summary>Tests memory persistence across session resets.</summary>
    CrossSession,
    /// <summary>Tests information retention after context reduction.</summary>
    ReducerFidelity,
    /// <summary>Tests agent correctly refuses to answer unanswerable questions (no hallucination).</summary>
    Abstention,
    /// <summary>Tests agent detects and resolves implicit contradictions (not explicit corrections).</summary>
    ConflictResolution,
    /// <summary>Tests synthesis of information across multiple sessions (requires ISessionResettableAgent).</summary>
    MultiSessionReasoning,
    /// <summary>Tests inference of user preferences from indirect behavioral signals (SSP-style).</summary>
    PreferenceExtraction
}
