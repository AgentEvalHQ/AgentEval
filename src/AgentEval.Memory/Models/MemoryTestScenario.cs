// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Represents a complete memory evaluation scenario with setup steps and verification queries.
/// A scenario defines a sequence of interactions followed by tests to verify memory retention.
/// </summary>
public class MemoryTestScenario
{
    /// <summary>
    /// Descriptive name for this memory test scenario.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description explaining what this scenario tests.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Sequence of conversation steps to establish facts in the agent's memory.
    /// These are executed in order before running any queries.
    /// </summary>
    public required IReadOnlyList<MemoryStep> Steps { get; init; }

    /// <summary>
    /// Memory verification queries to test what the agent remembers.
    /// These are executed after all setup steps are complete.
    /// </summary>
    public required IReadOnlyList<MemoryQuery> Queries { get; init; }

    /// <summary>
    /// Optional timeout for the entire scenario execution.
    /// If not specified, uses system defaults.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Optional metadata for additional context or test configuration.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Pre-built text blob containing conversation history (corpus + facts + noise) to prepend 
    /// to each verification query. When set, setup steps are skipped and this blob is used
    /// instead — matching the LongMemEval text-blob injection approach.
    /// </summary>
    public string? ContextTextBlob { get; set; }

    public override string ToString() => Name;

    /// <summary>
    /// Gets all facts referenced by queries in this scenario.
    /// </summary>
    public IEnumerable<MemoryFact> AllExpectedFacts => Queries.SelectMany(q => q.ExpectedFacts);

    /// <summary>
    /// Gets all facts that should be forbidden in query responses.
    /// </summary>
    public IEnumerable<MemoryFact> AllForbiddenFacts => Queries.SelectMany(q => q.ForbiddenFacts);

    /// <summary>
    /// Creates a simple scenario with alternating facts and noise steps.
    /// </summary>
    public static MemoryTestScenario Create(string name, IReadOnlyList<MemoryStep> steps, params MemoryQuery[] queries) => new()
    {
        Name = name,
        Steps = steps,
        Queries = queries.ToArray()
    };
}

/// <summary>
/// Represents a single step in a memory test scenario.
/// Can be a fact-establishing prompt, noise/distraction, or other interaction.
/// </summary>
public class MemoryStep
{
    /// <summary>
    /// The content to send to the agent (user message).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional timestamp when this step occurs.
    /// Used for temporal memory testing.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Type of step - fact establishment, noise, query, etc.
    /// </summary>
    public MemoryStepType Type { get; init; } = MemoryStepType.Fact;

    /// <summary>
    /// Optional expected response pattern (for validation).
    /// </summary>
    public string? ExpectedResponse { get; init; }

    /// <summary>
    /// Optional metadata for additional context.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    public override string ToString() => Content;

    /// <summary>
    /// Creates a fact-establishing step.
    /// </summary>
    public static MemoryStep Fact(string content) => new() { Content = content, Type = MemoryStepType.Fact };

    /// <summary>
    /// Creates a noise/distraction step.
    /// </summary>
    public static MemoryStep Noise(string content) => new() { Content = content, Type = MemoryStepType.Noise };

    /// <summary>
    /// Creates a general conversation step.
    /// </summary>
    public static MemoryStep Conversation(string content) => new() { Content = content, Type = MemoryStepType.Conversation };

    /// <summary>
    /// Creates a system instruction step.
    /// </summary>
    public static MemoryStep System(string content) => new() { Content = content, Type = MemoryStepType.System };

    /// <summary>
    /// Creates a temporal step with timestamp.
    /// </summary>
    public static MemoryStep Temporal(string content, DateTimeOffset timestamp) => new()
    {
        Content = content,
        Timestamp = timestamp,
        Type = MemoryStepType.Fact
    };
}

/// <summary>
/// Types of steps in a memory test scenario.
/// </summary>
public enum MemoryStepType
{
    /// <summary>Establishes a fact that should be remembered.</summary>
    Fact,
    /// <summary>Noise or distraction content.</summary>
    Noise,
    /// <summary>General conversation turn.</summary>
    Conversation,
    /// <summary>System instruction or setup.</summary>
    System
}