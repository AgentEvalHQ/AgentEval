// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Baseline;

/// <summary>
/// A saved RedTeam result used as a reference for comparison.
/// </summary>
public record RedTeamBaseline
{
    /// <summary>Version identifier (e.g., "v2.0.1", commit SHA).</summary>
    public required string Version { get; init; }

    /// <summary>When the baseline was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Name of the agent that was tested.</summary>
    public required string AgentName { get; init; }

    /// <summary>Overall security score (0-100).</summary>
    public required double OverallScore { get; init; }

    /// <summary>Attack success rate (lower is better).</summary>
    public required double AttackSuccessRate { get; init; }

    /// <summary>Total probes executed.</summary>
    public required int TotalProbes { get; init; }

    /// <summary>Probes that succeeded (vulnerabilities found).</summary>
    public required int SucceededProbes { get; init; }

    /// <summary>Results per attack type.</summary>
    public required IReadOnlyList<AttackBaselineResult> AttackResults { get; init; }

    /// <summary>List of known vulnerabilities (probe IDs that failed).</summary>
    public required IReadOnlyList<string> KnownVulnerabilities { get; init; }

    /// <summary>Scan intensity used for baseline.</summary>
    public Intensity Intensity { get; init; } = Intensity.Moderate;

    /// <summary>Optional notes about this baseline.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Creates a baseline from a RedTeam result.
    /// </summary>
    public static RedTeamBaseline FromResult(RedTeamResult result, string version, string? notes = null)
    {
        return new RedTeamBaseline
        {
            Version = version,
            CapturedAt = DateTimeOffset.UtcNow,
            AgentName = result.AgentName,
            OverallScore = result.OverallScore,
            AttackSuccessRate = result.AttackSuccessRate,
            TotalProbes = result.TotalProbes,
            SucceededProbes = result.SucceededProbes,
            AttackResults = result.AttackResults.Select(a => new AttackBaselineResult
            {
                AttackName = a.AttackName,
                AttackDisplayName = a.AttackDisplayName,
                OwaspId = a.OwaspId,
                Severity = a.Severity,
                ResistedCount = a.ResistedCount,
                TotalCount = a.TotalCount,
                FailedProbeIds = a.ProbeResults
                    .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                    .Select(p => p.ProbeId)
                    .ToList()
            }).ToList(),
            KnownVulnerabilities = result.FailedAttacks
                .SelectMany(a => a.ProbeResults.Where(p => p.Outcome == EvaluationOutcome.Succeeded))
                .Select(p => p.ProbeId)
                .ToList(),
            Intensity = result.Options?.Intensity ?? Intensity.Moderate,
            Notes = notes
        };
    }

    /// <summary>
    /// Loads a baseline from a JSON file.
    /// </summary>
    public static async Task<RedTeamBaseline> LoadAsync(string path, CancellationToken cancellationToken = default)
        => await BaselineSerializer.LoadAsync(path, cancellationToken);

    /// <summary>
    /// Saves this baseline to a JSON file.
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        => await BaselineSerializer.SaveAsync(this, path, cancellationToken);
}

/// <summary>
/// Baseline result for a single attack type.
/// </summary>
public record AttackBaselineResult
{
    /// <summary>Internal attack name (e.g., "PromptInjection").</summary>
    public required string AttackName { get; init; }

    /// <summary>Display name for the attack.</summary>
    public required string AttackDisplayName { get; init; }

    /// <summary>OWASP LLM Top 10 ID.</summary>
    public required string OwaspId { get; init; }

    /// <summary>Severity level.</summary>
    public required Severity Severity { get; init; }

    /// <summary>Number of probes resisted.</summary>
    public required int ResistedCount { get; init; }

    /// <summary>Total probes for this attack.</summary>
    public required int TotalCount { get; init; }

    /// <summary>IDs of probes that failed (vulnerabilities).</summary>
    public required IReadOnlyList<string> FailedProbeIds { get; init; }

    /// <summary>Resistance rate (higher is better).</summary>
    [JsonIgnore]
    public double Rate => TotalCount > 0 ? (double)ResistedCount / TotalCount : 1.0;
}
