// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>One tool's coverage classification, as produced by <see cref="GatekeeperCoverageAnalyzer"/>.</summary>
/// <param name="ToolName">The tool's name, as exposed to the model.</param>
/// <param name="Description">The tool's description, if any (used only for the risk heuristic).</param>
/// <param name="ExecutionModel">How the tool actually executes — see <see cref="ToolExecutionModel"/>.</param>
/// <param name="RiskLevel">The heuristic risk classification — see <see cref="ToolRiskLevel"/>.</param>
/// <param name="IsGateProtected">
/// Whether this tool's calls pass through at least one registered <see cref="IToolGate"/>. Always
/// <see langword="false"/> for anything other than <see cref="ToolExecutionModel.InterceptedLocalFunction"/> —
/// a provider-hosted or unknown-execution-model tool is never seen by any Gatekeeper tool gate, regardless of
/// how many are registered. This is a STRUCTURAL statement ("is the interception seam wired up at all"), not a
/// semantic one ("does some gate specifically defend this exact tool") — per-tool capability binding is a
/// separate, larger piece of work (see <see cref="ToolRiskLevel"/> remarks).
/// </param>
/// <param name="Note">A one-line, human-readable explanation for the report.</param>
public sealed record ToolCoverageEntry(
    string ToolName,
    string? Description,
    ToolExecutionModel ExecutionModel,
    ToolRiskLevel RiskLevel,
    bool IsGateProtected,
    string Note)
{
    /// <summary>True when this entry is a risk the report should call out: high-risk and not gate-protected.</summary>
    public bool IsUnprotectedHighRisk => RiskLevel == ToolRiskLevel.HighRisk && !IsGateProtected;
}
