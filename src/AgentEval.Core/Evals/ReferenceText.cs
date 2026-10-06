// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Evals;

/// <summary>
/// The one test for "a reference answer was supplied", shared by <see cref="F1ScoreEval"/> and the agentic evaluators that
/// send a reference to their judge: a reference with no word in it (blank, "?", "...") is none. F1 and similarity used
/// different tests, so a wordless reference was "none" for one and graded by the other (#203 review round 16, B12n).
/// </summary>
internal static class ReferenceText
{
    private static readonly char[] s_punctuation = ['.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']'];

    /// <summary>Whether <paramref name="text"/> has at least one word to compare against.</summary>
    public static bool HasWords([NotNullWhen(true)] string? text) => Tokenize(text).Count > 0;

    /// <summary>Whitespace-split, lowercased tokens with attached punctuation stripped; a multiset (duplicates kept).</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant().Trim(s_punctuation))
            .Where(t => t.Length > 0)
            .ToList();
    }
}
