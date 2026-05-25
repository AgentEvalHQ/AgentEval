// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Benchmarks;

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
    /// <remarks>
    /// P0-2 (Sprint 0): the runner resolves scenario JSON / corpus selection / reach-back depths
    /// using <see cref="PresetResolutionKey"/>; for Diagnostic this is "Full" so the JSON-driven
    /// preset chain (Quick → Standard → Full) and the context-stress corpus are actually loaded
    /// instead of silently degrading to Quick (which never declared "diagnostic" in any JSON).
    /// </remarks>
    public static MemoryBenchmark Diagnostic => new()
    {
        Name = "Diagnostic",
        PresetResolutionKey = "Full",
        Description = "Maximum context pressure diagnostic (11 categories, ~50K+ token context)",
        Categories = Full.Categories
    };

    /// <summary>
    /// Overflow benchmark: same 8 categories as Standard but with target_tokens set to 192K,
    /// forcing context overflow on models with 128K windows (e.g., GPT-4o-mini).
    /// Tests the agent's memory architecture (reducer, vector store) rather than LLM attention.
    /// </summary>
    /// <remarks>
    /// P0-2 (Sprint 0): resolution key routed to "Full" so JSON scenarios + corpus stress chain
    /// load instead of silently falling back to Quick. <see cref="TargetTokensOverride"/> raised
    /// from 128_000 → 192_000 to match the documented "192K total target, 75% (~144K) injected
    /// via history blob, remaining ~48K driven via real InvokeAsync overflow calls" semantic and
    /// to actually force overflow on a 128K-window model.
    /// </remarks>
    public static MemoryBenchmark Overflow => new()
    {
        Name = "Overflow",
        PresetResolutionKey = "Full",
        Description = "Gradual context overflow — fills 75% of 192K via injection, then filler calls push past the 128K window.",
        Categories = Standard.Categories,
        TargetTokensOverride = 192_000,
        OverflowCallsOverride = 20
    };

    /// <summary>
    /// Internal preset resolution key used by the runner when looking up scenario JSON,
    /// selecting corpus stress level, and choosing reach-back depths. Defaults to
    /// <see cref="Name"/> but presets such as Diagnostic / Overflow override it to
    /// route resolution through an existing JSON preset ("Full") while keeping the
    /// public <see cref="Name"/> stable for reports and CLI selection.
    /// </summary>
    /// <remarks>
    /// Without this split, <see cref="Name"/> "Diagnostic" flowed into
    /// <c>ScenarioLoader.ResolvePreset</c> which silently fell back to "quick" when
    /// no JSON declared a "diagnostic" key — making the marquee 50K+ token claim a
    /// false advertisement (the runner actually used the ~8K context-small corpus).
    /// </remarks>
    public string? PresetResolutionKey { get; init; }

    /// <summary>
    /// Returns the resolved preset key the runner should pass to
    /// <c>ScenarioLoader.ResolvePreset</c> and the per-category preset switches.
    /// </summary>
    internal string EffectivePresetResolutionKey => PresetResolutionKey ?? Name;

    /// <summary>
    /// When set, overrides context_pressure.target_tokens for all categories in this benchmark.
    /// Used by the Overflow preset to force context beyond model limits.
    /// </summary>
    public int? TargetTokensOverride { get; init; }

    /// <summary>
    /// When set, overrides context_pressure.overflow_calls for all categories.
    /// Sends this many filler turns via InvokeAsync after history injection to gradually
    /// fill the remaining context and trigger the agent's reducer.
    /// </summary>
    public int? OverflowCallsOverride { get; init; }
}
