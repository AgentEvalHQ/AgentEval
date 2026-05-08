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

        if (Directory.Exists(Path.Combine(workspaceRoot, ".AgentEval")))
            yield return new Finding(".AgentEval/", "Legacy uppercase folder; rename to .agenteval/");

        if (Directory.Exists(Path.Combine(workspaceRoot, "TestResults", "traces")))
            yield return new Finding("TestResults/traces/", "Legacy trace artifact location; move to .agenteval/subjects/.../runs/.../traces/");

        var legacyBench = Path.Combine(workspaceRoot, ".agenteval", "benchmarks");
        if (Directory.Exists(legacyBench))
            yield return new Finding(".agenteval/benchmarks/", "Legacy memory-benchmark location; should be migrated to .agenteval/subjects/agents/{Name}/");
    }
}
