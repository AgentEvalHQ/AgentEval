// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.Cli.Commands;

/// <summary>
/// Shared CI exit-code mapping for the bench family commands (<c>bench owasp|mitre|nist|gdpr|eu-ai-act|agentic|memory</c>)
/// (L23 / MNT-02). Previously each command inlined a near-identical composite-label switch; they now share one
/// mapping so a future change to the pass/fail policy lands in a single place.
/// </summary>
internal static class BenchExitCodes
{
    /// <summary>
    /// Maps a composite score label to a CI exit code (BUG-22 resolution — each gate outcome now gets its own
    /// code instead of being lumped into <see cref="ExitCodes.UsageError"/>): <c>pass</c> → 0,
    /// <c>warn</c> → <see cref="ExitCodes.GateWarning"/> (10), <c>fail</c> → <see cref="ExitCodes.GateFailed"/> (9),
    /// anything else (e.g. <c>skipped</c>) → <see cref="ExitCodes.GateIndeterminate"/> (11).
    /// </summary>
    public static int FromLabel(string label) => label.ToLowerInvariant() switch
    {
        "pass" => ExitCodes.Success,
        "warn" => ExitCodes.GateWarning,
        "fail" => ExitCodes.GateFailed,
        _      => ExitCodes.GateIndeterminate,
    };
}
