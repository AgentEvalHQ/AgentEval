// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
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

    /// <summary>
    /// Number of session boundaries to insert into the corpus.
    /// When > 0, corpus turns are divided into this many segments with session markers between them.
    /// </summary>
    [JsonPropertyName("sessions_count")]
    public int? SessionsCount { get; set; }

    /// <summary>
    /// Target total tokens for context pressure. When set, the corpus is repeated
    /// enough times to reach this token count, overriding max_turns.
    /// Used for overflow testing — set to 1.5x the model's context window
    /// to force the agent's memory/reducer system to activate.
    /// </summary>
    [JsonPropertyName("target_tokens")]
    public int? TargetTokens { get; set; }

    /// <summary>
    /// Themed distractor turns injected into the corpus at deterministic positions.
    /// These are topically SIMILAR to planted facts but contain WRONG details,
    /// forcing the agent to discriminate between its own facts and similar filler.
    /// </summary>
    [JsonPropertyName("distractor_turns")]
    public List<DistractorTurn>? DistractorTurns { get; set; }
}

/// <summary>
/// A distractor turn that is topically related to a planted fact but attributed
/// to someone else or containing different details. Used for semantic interference.
/// </summary>
public class DistractorTurn
{
    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("assistant")]
    public string Assistant { get; set; } = "";

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }
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

    /// <summary>
    /// Where to bury this fact within the corpus context.
    /// String values: "early" (10%), "middle" (50%), "late" (85%).
    /// Numeric values: 0.0-1.0 = fractional position through the corpus.
    /// Null = appended after corpus (current behavior, backwards compatible).
    /// </summary>
    [JsonPropertyName("position")]
    public JsonElement? Position { get; set; }

    /// <summary>
    /// Optional timestamp for temporal context (e.g., "2025-10-03").
    /// When set, the date is prefixed to the planted message: "[2025-10-03] Oh I just got promoted..."
    /// Null means no date prefix (backwards compatible).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Optional session number (1-based) this fact belongs to.
    /// When sessions are enabled, the fact is placed within the specified session segment
    /// instead of using fractional position across the entire corpus.
    /// </summary>
    [JsonPropertyName("session_id")]
    public int? SessionId { get; set; }

    /// <summary>
    /// Optional custom assistant response when this fact is planted.
    /// Used for assistant-memory facts where the assistant acknowledges with a richer reply.
    /// If null, defaults to "Got it, I'll remember that."
    /// </summary>
    [JsonPropertyName("assistant_response")]
    public string? AssistantResponse { get; set; }

    /// <summary>
    /// Resolves the position to a fraction (0.0-1.0). Returns null if no position set.
    /// </summary>
    [JsonIgnore]
    public double? FractionalPosition => Position?.ValueKind switch
    {
        JsonValueKind.String => Position.Value.GetString() switch
        {
            "early" => 0.1,
            "middle" => 0.5,
            "late" => 0.85,
            _ => null
        },
        JsonValueKind.Number => Position.Value.GetDouble(),
        _ => null
    };
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

    /// <summary>
    /// Type-specific judge prompt selector: "standard", "temporal", "preference", "update", "abstention".
    /// When null, defaults to "standard" (current behavior). When set to "abstention", also triggers abstention mode.
    /// </summary>
    [JsonPropertyName("query_type")]
    public string? QueryType { get; set; }
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
