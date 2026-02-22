// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Intensity of red-team scanning - controls number and difficulty of probes.
/// </summary>
public enum Intensity
{
    /// <summary>Quick scan with ~5 probes per attack (easy difficulty only).</summary>
    Quick = 0,

    /// <summary>Standard scan with ~15 probes per attack (easy + moderate).</summary>
    Moderate = 1,

    /// <summary>Thorough scan with all probes including hard difficulty.</summary>
    Comprehensive = 2
}

/// <summary>
/// Difficulty level of an attack probe.
/// </summary>
public enum Difficulty
{
    /// <summary>Basic attacks that most models should resist.</summary>
    Easy = 0,

    /// <summary>More sophisticated attacks requiring better defenses.</summary>
    Moderate = 1,

    /// <summary>Advanced attacks using encoding, obfuscation, multi-step.</summary>
    Hard = 2
}

/// <summary>
/// Overall verdict for a red-team scan.
/// </summary>
public enum Verdict
{
    /// <summary>All probes resisted - no vulnerabilities found.</summary>
    Pass = 0,

    /// <summary>Some probes succeeded - vulnerabilities present.</summary>
    Fail = 1,

    /// <summary>Most probes resisted but some edge cases failed.</summary>
    PartialPass = 2,

    /// <summary>Scan could not complete due to errors.</summary>
    Inconclusive = 3
}

/// <summary>
/// Severity of a detected vulnerability.
/// </summary>
public enum Severity
{
    /// <summary>Informational finding, no security impact.</summary>
    Informational = 0,

    /// <summary>Low severity - minor concern.</summary>
    Low = 1,

    /// <summary>Medium severity - should be addressed.</summary>
    Medium = 2,

    /// <summary>High severity - significant security risk.</summary>
    High = 3,

    /// <summary>Critical severity - immediate action required.</summary>
    Critical = 4
}

/// <summary>
/// Outcome of evaluating a single probe.
/// </summary>
public enum EvaluationOutcome
{
    /// <summary>The attack succeeded - agent was compromised.</summary>
    Succeeded = 0,

    /// <summary>The agent resisted the attack.</summary>
    Resisted = 1,

    /// <summary>Could not determine outcome.</summary>
    Inconclusive = 2
}
