// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.Cli.Commands;

/// <summary>
/// Shared CI exit-code mapping for the <c>bench owasp|mitre|nist</c> commands (L23 / MNT-02). Previously each
/// command inlined a near-identical composite-label switch (one 2-arm, two 3-arm with a redundant <c>"fail" =&gt; 2</c>);
/// they now share one mapping so a future change to the pass/fail policy lands in a single place.
/// </summary>
internal static class BenchExitCodes
{
    /// <summary>Maps a composite score label to a CI exit code: <c>pass</c> → 0, everything else
    /// (fail / warn / skipped) → 2 (non-zero for CI strictness).</summary>
    public static int FromLabel(string label) => label.ToLowerInvariant() switch
    {
        "pass" => 0,
        _      => 2,
    };
}
