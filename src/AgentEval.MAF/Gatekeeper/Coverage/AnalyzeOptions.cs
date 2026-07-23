// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Options for <see cref="GatekeeperCoverageAnalyzer"/>.</summary>
public sealed class AnalyzeOptions
{
    /// <summary>The default options: <see cref="ToolRiskClassifier.IsHighRisk"/> as the risk heuristic.</summary>
    public static AnalyzeOptions Default { get; } = new();

    /// <summary>
    /// The risk classifier. Defaults to <see cref="ToolRiskClassifier.IsHighRisk"/> (a keyword heuristic over
    /// the tool's name/description). Override this when the default heuristic misclassifies your tools.
    /// </summary>
    public Func<AITool, bool> IsHighRisk { get; init; } = ToolRiskClassifier.IsHighRisk;

    /// <summary>
    /// When <see langword="true"/> (the default), a provider-hosted opaque tool with <b>arbitrary capability</b>
    /// (a hosted code interpreter or a hosted MCP server) is classified <see cref="ToolRiskLevel.HighRisk"/>
    /// regardless of the keyword heuristic. Such a tool is <b>structurally uninterceptable</b> by any tool gate
    /// yet can execute arbitrary code / expose an arbitrary tool surface, so leaving it Standard-risk let
    /// <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(Microsoft.Agents.AI.AIAgent,System.Collections.Generic.IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>
    /// silently admit the single most dangerous class of tool (a fail-open the Fable 5 review confirmed). Set
    /// <see langword="false"/> only when a compensating control outside the gate pipeline governs these tools.
    /// </summary>
    public bool TreatArbitraryCapabilityOpaqueToolsAsHighRisk { get; init; } = true;
}
