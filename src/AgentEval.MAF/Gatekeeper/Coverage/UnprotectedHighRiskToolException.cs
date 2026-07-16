// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Thrown by <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(Microsoft.Agents.AI.AIAgent, IReadOnlyList{IToolGate}?, AnalyzeOptions?)"/>
/// (and its tool-list overload) when a tool the risk heuristic classifies as <see cref="ToolRiskLevel.HighRisk"/>
/// has zero protecting gate. Opt-in — call <c>AnalyzeOrThrow</c> yourself (typically right after
/// <c>.Build()</c>) to refuse to construct/start an agent whose high-risk tools are structurally unprotected.
/// </summary>
public sealed class UnprotectedHighRiskToolException : Exception
{
    /// <summary>The full coverage report that triggered this exception.</summary>
    public GatekeeperCoverageReport Report { get; }

    /// <summary>Creates the exception from the offending <paramref name="report"/>.</summary>
    public UnprotectedHighRiskToolException(GatekeeperCoverageReport report)
        : base(BuildMessage(report))
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }

    private static string BuildMessage(GatekeeperCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var names = string.Join(", ", report.Tools.Where(t => t.IsUnprotectedHighRisk).Select(t => t.ToolName));
        return $"Gatekeeper coverage check failed: {report.HighRiskUnprotectedCount} high-risk tool(s) have zero " +
               $"protecting gate ({names}). Register a tool gate that covers them, mark the tool as not high-risk " +
               "via AnalyzeOptions.IsHighRisk, or opt out of this check.";
    }
}
