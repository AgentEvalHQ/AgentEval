// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Interface for an attack type that generates probes to test an agent.
/// </summary>
/// <remarks>
/// Each attack type represents a category of security vulnerability
/// (e.g., PromptInjection, Jailbreak, PIILeakage).
/// Implementations provide the probes and evaluation logic.
/// </remarks>
public interface IAttackType
{
    /// <summary>
    /// Internal name of the attack (e.g., "PromptInjection").
    /// Used for filtering and configuration.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable display name (e.g., "Prompt Injection").
    /// Used in reports and UI.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Detailed description of this attack type.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// OWASP LLM Top 10 vulnerability identifier.
    /// Example: "LLM01" for prompt injection.
    /// </summary>
    string OwaspLlmId { get; }

    /// <summary>
    /// MITRE ATLAS technique identifiers for this attack.
    /// </summary>
    string[] MitreAtlasIds { get; }

    /// <summary>
    /// Default severity level for this attack type.
    /// </summary>
    Severity DefaultSeverity { get; }

    /// <summary>
    /// Get probes for the specified intensity level.
    /// </summary>
    /// <param name="intensity">The scan intensity level.</param>
    /// <returns>Collection of attack probes.</returns>
    IReadOnlyList<AttackProbe> GetProbes(Intensity intensity);

    /// <summary>
    /// Get the evaluator for analyzing agent responses.
    /// </summary>
    /// <returns>The probe evaluator for this attack type.</returns>
    IProbeEvaluator GetEvaluator();
}

/// <summary>
/// Extension methods for IAttackType.
/// </summary>
public static class AttackTypeExtensions
{
    /// <summary>
    /// Get all probes with default (Moderate) intensity.
    /// </summary>
    public static IReadOnlyList<AttackProbe> GetProbes(this IAttackType attackType)
    {
        ArgumentNullException.ThrowIfNull(attackType);
        return attackType.GetProbes(Intensity.Moderate);
    }

    /// <summary>
    /// Get probes limited to a maximum count.
    /// </summary>
    public static IReadOnlyList<AttackProbe> GetProbes(
        this IAttackType attackType,
        Intensity intensity,
        int maxProbes)
    {
        ArgumentNullException.ThrowIfNull(attackType);
        var probes = attackType.GetProbes(intensity);
        return maxProbes > 0 ? probes.Take(maxProbes).ToList() : probes;
    }
}
