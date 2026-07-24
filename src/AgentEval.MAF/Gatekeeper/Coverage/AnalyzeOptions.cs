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

    /// <summary>
    /// Tool <b>names</b> of provider-hosted tools whose risk you <b>explicitly accept</b> — each is exempted from
    /// the <see cref="TreatArbitraryCapabilityOpaqueToolsAsHighRisk"/> auto-escalation, so
    /// <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(Microsoft.Agents.AI.AIAgent,System.Collections.Generic.IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>
    /// will admit them. This is the sanctioned escape hatch (Fable 5 §2 follow-up) for a code-interpreter / MCP
    /// tool you have a compensating control for — a greppable, auditable opt-in rather than turning the whole
    /// escalation off. Names match by this set's own comparer (pass a case-insensitive set if you want that).
    /// </summary>
    public IReadOnlySet<string>? AcknowledgeProviderHostedTools { get; init; }

    /// <summary>
    /// Declare that the agent can contribute tools <b>dynamically</b> at invocation time via an
    /// <see cref="Microsoft.Agents.AI.AIContextProvider"/> (Agent Skills, a memory provider). The analyzer reads
    /// only the static <c>ChatOptions.Tools</c> list, so an agent whose tools come ONLY from such a provider would
    /// otherwise report a vacuous 100% coverage and <c>AnalyzeOrThrow</c> would silently certify it (Fable 5 §1, a
    /// high-severity fail-open). When this is set (or a provider is detected) and the static list is empty, the
    /// report is marked inventory-unavailable so <c>AnalyzeOrThrow</c> refuses to certify — "couldn't verify" fails
    /// the same direction as "verified-bad". Default <see langword="false"/> (unchanged for static-tools agents).
    /// </summary>
    public bool HasDynamicToolProvider { get; init; }
}
