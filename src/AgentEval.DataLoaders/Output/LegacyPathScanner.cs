// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Scans a workspace root for legacy AgentEval output paths that pre-date the canonical
/// <c>.agenteval/subjects/...</c> layout. Used by <c>agenteval doctor</c> and <c>agenteval migrate</c>.
/// </summary>
internal static class LegacyPathScanner
{
    /// <summary>Represents a detected legacy path and the reason it should be migrated.</summary>
    public sealed record Finding(string Path, string Reason);

    /// <summary>
    /// Scans <paramref name="workspaceRoot"/> for known legacy output locations.
    /// </summary>
    /// <param name="workspaceRoot">Absolute path to the workspace (project/solution root).</param>
    /// <returns>A sequence of <see cref="Finding"/> records for each legacy location detected.</returns>
    public static IEnumerable<Finding> Scan(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot)) yield break;

        var topLevel = new DirectoryInfo(workspaceRoot).GetDirectories();

        // Case-sensitive match against the on-disk name. On Windows the FS is
        // case-insensitive at lookup but case-preserving at create time, so a
        // directory created as ".AgentEval" reports its name as ".AgentEval"
        // even though Path.Combine(root, ".AgentEval") would also resolve to
        // an existing ".agenteval/" — that aliasing is what we must avoid here
        // to prevent a false positive on modern lowercase workspaces.
        if (topLevel.Any(d => string.Equals(d.Name, ".AgentEval", StringComparison.Ordinal)))
            yield return new Finding(".AgentEval/", "Legacy uppercase folder; rename to .agenteval/");

        if (Directory.Exists(Path.Combine(workspaceRoot, "TestResults", "traces")))
            yield return new Finding("TestResults/traces/", "Legacy trace artifact location; move to .agenteval/subjects/.../runs/.../traces/");

        var lowerAgentEval = topLevel.FirstOrDefault(d => string.Equals(d.Name, ".agenteval", StringComparison.Ordinal));
        if (lowerAgentEval is not null && Directory.Exists(Path.Combine(lowerAgentEval.FullName, "benchmarks")))
            yield return new Finding(".agenteval/benchmarks/", "Legacy memory-benchmark location; should be migrated to .agenteval/subjects/agents/{Name}/");
    }
}
