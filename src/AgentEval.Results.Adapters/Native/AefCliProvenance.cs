// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Observed CLI invocation identity, without arguments, machine names or working-directory paths.</summary>
public sealed record AefCliProvenance
{
    /// <summary>Registered command names, for example <c>bench gdpr</c>.</summary>
    public required string Verb { get; init; }
    /// <summary>Observed Git commit; absent outside a repository or before the first commit.</summary>
    public string? GitCommit { get; init; }
    /// <summary>Observed Git branch; absent when detached or unknown.</summary>
    public string? GitBranch { get; init; }
    /// <summary>Whether tracked or untracked changes were observed; null when Git status is unknown.</summary>
    public bool? GitDirty { get; init; }

    internal static JsonObject? Extension(AefCliProvenance? cli, AefStoreV1Link? store = null)
    {
        var ext = store?.ToExtension() ?? new JsonObject();
        if (cli is not null)
        {
            if (string.IsNullOrWhiteSpace(cli.Verb) || cli.Verb.Length > 256 ||
                cli.Verb.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not ' '))
                throw new ArgumentException("CLI provenance requires registered command names, not arguments or paths.");
            if (cli.GitCommit is { } commit && (commit.Length is not (40 or 64) || commit.Any(c => !Uri.IsHexDigit(c))))
                throw new ArgumentException("Git commit must be a full hexadecimal object ID.");
            if (cli.GitBranch is { } branch && (string.IsNullOrWhiteSpace(branch) || branch.Length > 256 || branch.Any(char.IsControl)))
                throw new ArgumentException("Git branch must be a nonempty bounded ref name.");
            var data = new JsonObject { ["verb"] = cli.Verb };
            if (cli.GitCommit is not null) data["gitCommit"] = cli.GitCommit;
            if (cli.GitBranch is not null) data["gitBranch"] = cli.GitBranch;
            if (cli.GitDirty is not null) data["gitDirty"] = cli.GitDirty.Value;
            ext["agenteval.cli"] = data;
        }
        return ext.Count == 0 ? null : ext;
    }
}
