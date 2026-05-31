// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Cli.Commands;

/// <summary>
/// Defense-in-depth canonicalisation + existence-check for the
/// <c>--root &lt;path&gt;</c> CLI parameter. The trust model is "operator-supplied
/// at the shell" — a hostile operator can already <c>rm -rf</c> directly — but
/// canonicalising before <c>Path.Combine</c> downstream stops accidental
/// <c>../</c> traversal and surfaces typos as exit-1 errors rather than
/// confusing not-found behaviour deep in the pipeline.
/// </summary>
internal static class WorkspaceRootValidator
{
    /// <summary>
    /// Canonicalises <paramref name="rootOverride"/> via <c>Path.GetFullPath</c>
    /// and verifies the directory exists. Returns the canonical path on success.
    /// On failure, writes a diagnostic to stderr and returns <c>null</c>; the
    /// caller should return exit code 1.
    /// </summary>
    /// <param name="rootOverride">Raw <c>--root</c> CLI value; <c>null</c> means "use auto-discovery".</param>
    /// <returns>Canonical existing path, or <c>null</c> if the override was bad.</returns>
    internal static string? CanonicaliseOrNull(string? rootOverride)
    {
        // MNT-03: the canonicalise + existence rule is shared with Mission Control via
        // WorkspaceRootDiscovery.CanonicaliseExistingDirectory; this method only owns the --root-specific
        // stderr wording. A blank override means "use auto-discovery" → null.
        var (canonical, status, detail) =
            AgentEval.Output.WorkspaceRootDiscovery.CanonicaliseExistingDirectory(rootOverride);
        switch (status)
        {
            case AgentEval.Output.WorkspaceRootDiscovery.PathStatus.Malformed:
                Console.Error.WriteLine($"--root path '{rootOverride}' is malformed: {detail}");
                return null;
            case AgentEval.Output.WorkspaceRootDiscovery.PathStatus.NotFound:
                Console.Error.WriteLine($"--root path '{rootOverride}' (canonicalised to '{detail}') does not exist.");
                return null;
            default:
                return canonical; // null when blank (auto-discovery), else the canonical existing path
        }
    }
}
