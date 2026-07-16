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
}
