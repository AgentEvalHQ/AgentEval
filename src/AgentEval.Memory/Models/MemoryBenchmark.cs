namespace AgentEval.Memory.Models;

/// <summary>
/// Defines a memory benchmark preset that specifies which scenarios to run and their weights.
/// </summary>
public class MemoryBenchmark
{
    /// <summary>
    /// Display name for this benchmark preset.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this benchmark tests.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Categories to include in this benchmark run.
    /// </summary>
    public required IReadOnlyList<MemoryBenchmarkCategory> Categories { get; init; }

    /// <summary>
    /// Quick benchmark: basic retention + temporal + noise (3 categories).
    /// Minimal test for fast CI feedback.
    /// </summary>
    public static MemoryBenchmark Quick => new()
    {
        Name = "Quick",
        Description = "Fast memory benchmark for CI pipelines (3 categories)",
        Categories =
        [
            new() { Name = "Basic Retention", Weight = 0.4, ScenarioType = BenchmarkScenarioType.BasicRetention },
            new() { Name = "Temporal Reasoning", Weight = 0.3, ScenarioType = BenchmarkScenarioType.TemporalReasoning },
            new() { Name = "Noise Resilience", Weight = 0.3, ScenarioType = BenchmarkScenarioType.NoiseResilience }
        ]
    };

    /// <summary>
    /// Standard benchmark: 7 categories covering the most important memory capabilities.
    /// Includes Abstention (hallucination detection).
    /// </summary>
    public static MemoryBenchmark Standard => new()
    {
        Name = "Standard",
        Description = "Comprehensive memory benchmark (8 categories, includes Abstention + Preference Extraction)",
        Categories =
        [
            new() { Name = "Basic Retention", Weight = 0.16, ScenarioType = BenchmarkScenarioType.BasicRetention },
            new() { Name = "Temporal Reasoning", Weight = 0.12, ScenarioType = BenchmarkScenarioType.TemporalReasoning },
            new() { Name = "Noise Resilience", Weight = 0.12, ScenarioType = BenchmarkScenarioType.NoiseResilience },
            new() { Name = "Reach-Back Depth", Weight = 0.16, ScenarioType = BenchmarkScenarioType.ReachBackDepth },
            new() { Name = "Fact Update Handling", Weight = 0.12, ScenarioType = BenchmarkScenarioType.FactUpdateHandling },
            new() { Name = "Multi-Topic", Weight = 0.10, ScenarioType = BenchmarkScenarioType.MultiTopic },
            new() { Name = "Abstention", Weight = 0.10, ScenarioType = BenchmarkScenarioType.Abstention },
            new() { Name = "Preference Extraction", Weight = 0.12, ScenarioType = BenchmarkScenarioType.PreferenceExtraction }
        ]
    };

    /// <summary>
    /// Full benchmark: all 11 categories including cross-session, reducer, abstention, conflict resolution, and multi-session reasoning.
    /// Requires agent to implement ISessionResettableAgent for full results.
    /// </summary>
    public static MemoryBenchmark Full => new()
    {
        Name = "Full",
        Description = "Complete memory benchmark suite (12 categories)",
        Categories =
        [
            new() { Name = "Basic Retention", Weight = 0.10, ScenarioType = BenchmarkScenarioType.BasicRetention },
            new() { Name = "Temporal Reasoning", Weight = 0.08, ScenarioType = BenchmarkScenarioType.TemporalReasoning },
            new() { Name = "Noise Resilience", Weight = 0.08, ScenarioType = BenchmarkScenarioType.NoiseResilience },
            new() { Name = "Reach-Back Depth", Weight = 0.10, ScenarioType = BenchmarkScenarioType.ReachBackDepth },
            new() { Name = "Fact Update Handling", Weight = 0.07, ScenarioType = BenchmarkScenarioType.FactUpdateHandling },
            new() { Name = "Multi-Topic", Weight = 0.07, ScenarioType = BenchmarkScenarioType.MultiTopic },
            new() { Name = "Cross-Session", Weight = 0.10, ScenarioType = BenchmarkScenarioType.CrossSession },
            new() { Name = "Reducer Fidelity", Weight = 0.10, ScenarioType = BenchmarkScenarioType.ReducerFidelity },
            new() { Name = "Abstention", Weight = 0.08, ScenarioType = BenchmarkScenarioType.Abstention },
            new() { Name = "Conflict Resolution", Weight = 0.07, ScenarioType = BenchmarkScenarioType.ConflictResolution },
            new() { Name = "Multi-Session Reasoning", Weight = 0.07, ScenarioType = BenchmarkScenarioType.MultiSessionReasoning },
            new() { Name = "Preference Extraction", Weight = 0.08, ScenarioType = BenchmarkScenarioType.PreferenceExtraction }
        ]
    };

    /// <summary>
    /// Diagnostic benchmark: same categories as Full but with maximum context pressure (~50K+ tokens).
    /// Use for deep analysis of memory limits. Takes significantly longer to run.
    /// </summary>
    public static MemoryBenchmark Diagnostic => new()
    {
        Name = "Diagnostic",
        Description = "Maximum context pressure diagnostic (11 categories, ~50K+ token context)",
        Categories = Full.Categories
    };
}

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
