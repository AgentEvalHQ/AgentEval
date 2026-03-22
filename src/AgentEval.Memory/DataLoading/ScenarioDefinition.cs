// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.DataLoading;

/// <summary>
/// Root model for a benchmark scenario JSON file.
/// Each file defines one benchmark category with Quick/Standard/Full presets.
/// </summary>
public class ScenarioDefinition
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("context_pressure")]
    public ContextPressureConfig? ContextPressure { get; set; }

    [JsonPropertyName("presets")]
    public Dictionary<string, PresetDefinition> Presets { get; set; } = new();
}

/// <summary>
/// Context pressure configuration — which corpus to inject before planting facts.
/// </summary>
public class ContextPressureConfig
{
    [JsonPropertyName("corpus")]
    public string Corpus { get; set; } = "context-small";

    [JsonPropertyName("max_turns")]
    public int? MaxTurns { get; set; }
}

/// <summary>
/// A preset (Quick/Standard/Full) within a scenario.
/// Supports inheritance: Standard can extend Quick, Full can extend Standard.
/// </summary>
public class PresetDefinition
{
    [JsonPropertyName("extends")]
    public string? Extends { get; set; }

    [JsonPropertyName("facts")]
    public List<FactDefinition>? Facts { get; set; }

    [JsonPropertyName("noise_between_facts")]
    public List<string>? NoiseBetweenFacts { get; set; }

    [JsonPropertyName("queries")]
    public List<QueryDefinition>? Queries { get; set; }

    [JsonPropertyName("context_pressure")]
    public ContextPressureConfig? ContextPressure { get; set; }
}

/// <summary>
/// A fact to plant in the agent's memory during a scenario.
/// </summary>
public class FactDefinition
{
    /// <summary>The core fact content (used by the judge for scoring).</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>Optional category grouping.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Importance level (0-100). Higher = more critical to recall.</summary>
    [JsonPropertyName("importance")]
    public int Importance { get; set; } = 50;

    /// <summary>
    /// How the fact is communicated to the agent (natural phrasing).
    /// If null, Content is used directly.
    /// </summary>
    [JsonPropertyName("planted_as")]
    public string? PlantedAs { get; set; }
}

/// <summary>
/// A query to test fact recall.
/// </summary>
public class QueryDefinition
{
    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("expected_facts")]
    public List<string> ExpectedFacts { get; set; } = [];

    [JsonPropertyName("forbidden_facts")]
    public List<string>? ForbiddenFacts { get; set; }

    /// <summary>Query difficulty: "direct", "indirect", "synthesis", "abstention".</summary>
    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; set; }

    /// <summary>Whether this is an abstention query (agent should say "I don't know").</summary>
    [JsonPropertyName("abstention")]
    public bool Abstention { get; set; }
}

/// <summary>
/// A resolved preset with all inherited facts/queries merged.
/// This is what the runner actually uses — no inheritance to resolve at runtime.
/// </summary>
public class ResolvedPreset
{
    public required List<FactDefinition> Facts { get; init; }
    public required List<string> NoiseBetweenFacts { get; init; }
    public required List<QueryDefinition> Queries { get; init; }
    public ContextPressureConfig? ContextPressure { get; init; }
}
