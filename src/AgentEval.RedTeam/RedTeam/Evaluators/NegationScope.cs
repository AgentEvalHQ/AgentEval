// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Jun14v2 (H2/H3): shared clause-scoped negation check. A keyword match (a planted false fact, a bare compliance verb)
/// counts as a genuine assertion only if its enclosing clause is NOT negated. Unlike a fixed "N chars BEFORE the match"
/// window, this scopes to sentence/clause delimiters on BOTH sides, so a trailing "…, not the capital" or a leading
/// "Sorry, I won't …" is detected — the exact gap that fabricated a Succeeded on a correct rejection / unrecognised
/// refusal. Pure function; inputs are expected already lower-cased.
/// </summary>
internal static class NegationScope
{
    /// <summary>
    /// True when the clause enclosing <c>[matchStart, matchStart+matchLength)</c> in <paramref name="lower"/> contains
    /// any of <paramref name="negationMarkers"/>. The clause runs from the nearest preceding clause boundary to the
    /// nearest following one (or the text bounds).
    /// </summary>
    public static bool ClauseIsNegated(string lower, int matchStart, int matchLength, IReadOnlyList<string> negationMarkers)
    {
        if (matchStart < 0 || matchStart >= lower.Length) return false;
        var start = 0;
        for (var i = matchStart - 1; i >= 0; i--)
            if (IsClauseBoundary(lower, i)) { start = i + 1; break; }
        var end = lower.Length;
        for (var i = matchStart + matchLength; i < lower.Length; i++)
            if (IsClauseBoundary(lower, i)) { end = i; break; }
        var clause = lower[start..end];
        return negationMarkers.Any(n => clause.Contains(n, StringComparison.Ordinal));
    }

    private static readonly char[] ClosingPunctuation = ['"', '\'', '”', '’', ')', ']', '}', '»'];

    // Jun14v3 + review-fix: ';' and newline always end a clause; '.', '!', '?' do so when followed by whitespace/end OR a
    // run of CLOSING PUNCTUATION (quote/paren) that is itself followed by whitespace/end — so `revoked." The old policy…`
    // and `(…done.)` split correctly. A dot inside a token (email "company.com", decimal "3.14", abbreviation) still does
    // NOT split — there the next char is a letter/digit, neither whitespace nor closing punctuation.
    private static bool IsClauseBoundary(string s, int i)
    {
        var c = s[i];
        if (c is ';' or '\n') return true;
        if (c is '.' or '!' or '?')
        {
            var j = i + 1;
            while (j < s.Length && Array.IndexOf(ClosingPunctuation, s[j]) >= 0) j++;
            return j >= s.Length || char.IsWhiteSpace(s[j]);
        }
        return false;
    }
}
