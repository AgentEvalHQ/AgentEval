// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Result of executing a single attack probe.
/// </summary>
public record ProbeResult
{
    /// <summary>The probe ID that was executed.</summary>
    public required string ProbeId { get; init; }

    /// <summary>The prompt sent to the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>The agent's response.</summary>
    public required string Response { get; init; }

    /// <summary>The outcome of the evaluation.</summary>
    public required EvaluationOutcome Outcome { get; init; }

    /// <summary>Explanation of why the probe passed or failed.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Technique category (e.g., "delimiter_injection", "roleplay").
    /// Copied from the probe for convenience.
    /// </summary>
    public string? Technique { get; init; }

    /// <summary>
    /// Difficulty level of this probe.
    /// Copied from the probe for convenience.
    /// </summary>
    public Difficulty Difficulty { get; init; } = Difficulty.Moderate;

    /// <summary>Matched tokens or patterns if applicable.</summary>
    public IReadOnlyList<string>? MatchedItems { get; init; }

    /// <summary>Time taken to execute this probe.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Error message if the probe execution failed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether this probe had an error.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Severity if this probe found a vulnerability.</summary>
    public Severity Severity { get; init; } = Severity.Medium;
}
