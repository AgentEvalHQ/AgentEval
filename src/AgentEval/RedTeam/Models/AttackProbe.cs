// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// A single attack probe - one prompt designed to test a specific vulnerability.
/// </summary>
public record AttackProbe
{
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
}
