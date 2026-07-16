// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A coarse, heuristic risk classification for a tool, used only to prioritize the coverage report and to
/// drive the optional "refuse to start unprotected" check (<see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(Microsoft.Agents.AI.AIAgent, IReadOnlyList{IToolGate}?, AnalyzeOptions?)"/>).
/// This is deliberately a name/description keyword heuristic, not a capability model — a real per-tool
/// capability descriptor (read/write/network/monetary/…) is a larger, separate piece of work (the Gatekeeper
/// review's finding #8, "weak tool-name identity") and out of scope here. Override the default via
/// <see cref="AnalyzeOptions.IsHighRisk"/> when the heuristic is wrong for your tools.
/// </summary>
public enum ToolRiskLevel
{
    /// <summary>No mutating/exfiltrating/monetary keyword matched the tool's name or description.</summary>
    Standard,

    /// <summary>
    /// The tool's name or description matched a keyword commonly associated with a mutating, financial, or
    /// data-exfiltrating action (e.g. "delete", "send", "pay", "transfer", "execute"). A heuristic signal,
    /// not a proof — false positives and false negatives are both expected.
    /// </summary>
    HighRisk,
}
