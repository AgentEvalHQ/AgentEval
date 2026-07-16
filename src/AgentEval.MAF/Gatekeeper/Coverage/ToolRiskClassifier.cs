// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The default <see cref="ToolRiskLevel"/> heuristic used by <see cref="GatekeeperCoverageAnalyzer"/>: a
/// tool's name/description is flagged <see cref="ToolRiskLevel.HighRisk"/> when it contains a keyword commonly
/// associated with a mutating, financial, or data-exfiltrating action. Deliberately coarse — a plain
/// case-insensitive substring match, simpler than the compiled-regex scanning <c>ArgumentPatternGate</c>/
/// <c>DomainAllowListGate</c> use elsewhere in Gatekeeper (this classifier only ever looks at a tool's own
/// short name/description, not arbitrary call arguments, so a regex engine buys nothing here) — override via
/// <see cref="AnalyzeOptions.IsHighRisk"/> when it misclassifies your tools. A real capability model
/// (read/write/network/monetary…) is future work — see <see cref="ToolRiskLevel"/> remarks.
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
        // Sensitive-data / credential access — nouns, not generic read verbs ("read"/"get"/"fetch" alone would
        // flag nearly every read-only tool; a keyword confirmed missing entirely, letting the canonical
        // "read_secrets" example tool used throughout this framework's own tests go unclassified as high-risk).
        "secret", "credential", "password", "passwd", "token", "apikey", "api_key", "privatekey", "private_key",
        "ssn", "pii", "confidential",
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
