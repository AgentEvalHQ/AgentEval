// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// A single attack probe - one prompt designed to test a specific vulnerability.
/// </summary>
public record AttackProbe
{
    /// <summary>
    /// <see cref="Metadata"/> key (value <c>true</c>) marking a probe whose technique a text chat agent
    /// physically cannot exercise (e.g. setting inference parameters, opening a stream, invoking a tool).
    /// The runner records such probes as <see cref="EvaluationOutcome.Inconclusive"/> instead of scoring a
    /// fabricated pass/fail (T5-4 / Status §8).
    /// </summary>
    public const string StructurallyUntestableMetadataKey = "structurally_untestable";

    /// <summary>Unique identifier for this probe (e.g., "PI-001").</summary>
    public required string Id { get; init; }

    /// <summary>The attack prompt to send to the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>Difficulty level of this probe.</summary>
    public required Difficulty Difficulty { get; init; }

    /// <summary>
    /// Name of the attack type this probe belongs to.
    /// Set automatically when added to an attack.
    /// </summary>
    public string? AttackName { get; init; }

    /// <summary>Source attribution (e.g., "garak pattern", "OWASP example").</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Technique category (e.g., "delimiter_injection", "roleplay").
    /// Useful for grouping in reports.
    /// </summary>
    public string? Technique { get; init; }

    /// <summary>Expected tokens that indicate successful attack.</summary>
    public IReadOnlyList<string>? ExpectedTokens { get; init; }

    /// <summary>Additional metadata for custom processing.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Delivery surface for an injection probe (Wave B, Pillar 4). <c>null</c> for probes where the concept does not
    /// apply; an injection attack labels each probe <see cref="InjectionSurface.UserMessage"/> (inlined proxy) or a
    /// real-boundary surface (<see cref="InjectionSurface.ToolOutput"/> / <see cref="InjectionSurface.RetrievedDocument"/>).
    /// </summary>
    public InjectionSurface? Surface { get; init; }
}
