// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.Assertions;

/// <summary>
/// Glass Box Phase 3 (P3.3-pre) — a shared, ReDoS-safe forbidden-content scan, factored out of the inline
/// scan in <see cref="ToolUsageAssertions"/> so a scoring evaluator (e.g. ArgumentSanitizationEval) can reuse
/// the fail-closed matching logic without invoking the side-effecting assertion.
/// </summary>
public static class ForbiddenPatternScanner
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="text"/> matches <paramref name="pattern"/> (a forbidden-content
    /// hit), OR when the match times out — <b>fail-closed</b>: a timeout means we could not prove the text
    /// clean, so it is treated as a hit. Pass a <see cref="Regex"/> constructed with a bounded
    /// <see cref="Regex.MatchTimeout"/> (e.g. 1s) so a catastrophic-backtracking pattern over
    /// attacker-influenced input cannot hang the evaluation thread (ReDoS).
    /// </summary>
    public static bool ScanForForbiddenPattern(string? text, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Nothing to scan == clean. Guard BEFORE matching so a permissive user regex (e.g. ".*", which
        // matches the empty string) cannot report a forbidden hit on absent/empty text.
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // fail-closed
        }
    }
}
