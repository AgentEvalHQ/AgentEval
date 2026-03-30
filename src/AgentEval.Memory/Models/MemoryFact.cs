// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Represents a single unit of information that an agent should remember.
/// Facts can have temporal context and relationships to other facts.
/// </summary>
public record MemoryFact
{
    /// <summary>
    /// The actual content/information that should be remembered.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional timestamp when this fact was established or became relevant.
    /// Used for temporal memory evaluation.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Optional category or topic this fact belongs to.
    /// Helps organize facts and create targeted queries.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Importance level of this fact (0-100).
    /// Higher values indicate more critical information that should be retained longer.
    /// </summary>
    public int Importance { get; init; } = 50;

    /// <summary>
    /// Optional metadata for additional context or test configuration.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    public override string ToString() => Content;

    /// <summary>
    /// Creates a simple fact with just content.
    /// </summary>
    public static MemoryFact Create(string content) => new() { Content = content };

    /// <summary>
    /// Creates a fact with content and timestamp.
    /// </summary>
    public static MemoryFact Create(string content, DateTimeOffset timestamp) => new()
    {
        Content = content,
        Timestamp = timestamp
    };

    /// <summary>
    /// Creates a categorized fact with importance level.
    /// </summary>
    public static MemoryFact Create(string content, string category, int importance = 50) => new()
    {
        Content = content,
        Category = category,
        Importance = importance
    };
}