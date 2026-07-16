// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The default <see cref="ToolRiskLevel"/> heuristic used by <see cref="GatekeeperCoverageAnalyzer"/>: a
/// tool's name/description is flagged <see cref="ToolRiskLevel.HighRisk"/> when it contains a keyword commonly
/// associated with a mutating, financial, or data-exfiltrating action. Deliberately coarse (a keyword
/// substring match, the same class of heuristic <c>ArgumentPatternGate</c>/<c>DomainAllowListGate</c> already
/// use elsewhere in Gatekeeper) — override via <see cref="AnalyzeOptions.IsHighRisk"/> when it misclassifies
/// your tools. A real capability model (read/write/network/monetary…) is future work — see
/// <see cref="ToolRiskLevel"/> remarks.
/// </summary>
public static class ToolRiskClassifier
{
    private static readonly string[] Keywords =
    [
        // Destructive
        "delete", "remove", "drop", "destroy", "purge", "wipe", "erase", "truncate",
        // Exfiltration / outbound communication
        "send", "email", "post", "publish", "notify", "broadcast", "upload", "export",
        // Financial
        "pay", "transfer", "refund", "charge", "purchase", "buy", "wire", "withdraw", "deposit", "invoice", "billing",
        // Mutation
        "write", "update", "modify", "edit", "overwrite", "create", "insert", "patch",
        // Execution
        "execute", "exec", "run_command", "run_script", "runcommand", "runscript", "shell", "eval", "spawn",
        // Privilege / lifecycle
        "revoke", "grant", "admin", "sudo", "terminate", "cancel", "unsubscribe", "shutdown", "restart", "format", "deploy",
    ];

    /// <summary>Returns whether <paramref name="tool"/>'s name or description matches a high-risk keyword.</summary>
    public static bool IsHighRisk(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return ContainsKeyword(tool.Name) || ContainsKeyword(tool.Description);
    }

    private static bool ContainsKeyword(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var keyword in Keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
