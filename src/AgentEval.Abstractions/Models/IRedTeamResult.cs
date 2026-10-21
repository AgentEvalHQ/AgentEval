// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Summary interface for red-team scan results.
/// Enables <see cref="TestResult"/> to reference red-team results without depending
/// on the full RedTeam subsystem and its transitive dependencies.
/// </summary>
/// <remarks>
/// <para>
/// The concrete <c>RedTeamResult</c> class (in <c>AgentEval.RedTeam</c>) implements this interface.
/// Code that needs access to detailed attack results can cast:
/// <code>var detail = (RedTeamResult)testResult.RedTeam;</code>
/// </para>
/// <para>See Phase 0.4 of the modularization plan for architectural rationale.</para>
/// </remarks>
public interface IRedTeamResult
{
    /// <summary>Name of the agent that was tested.</summary>
    string AgentName { get; }

    /// <summary>Overall security score (0-100). Higher is better. <see langword="null"/> when no probe reached a verdict: the scan measured nothing (T81).</summary>
    double? OverallScore { get; }

    /// <summary>Whether the scan passed (no vulnerabilities found).</summary>
    bool Passed { get; }

    /// <summary>Human-readable summary.</summary>
    string Summary { get; }

    /// <summary>Attack Success Rate (0.0 = no attack succeeded, 1.0 = fully compromised). <see langword="null"/> when no probe reached a verdict (T81).</summary>
    double? AttackSuccessRate { get; }
}
