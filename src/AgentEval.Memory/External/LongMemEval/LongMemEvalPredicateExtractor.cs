// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Splits a gold answer into the individual claims a response must support under
/// <see cref="Models.JudgeDecompositionMode.PerPredicate"/>.
/// </summary>
/// <remarks>
/// <para><b>Deterministic on purpose.</b> Extraction could be done with an LLM call, which would split
/// more naturally but would add a provider call per question and make the predicate set itself
/// non-reproducible — the decomposition would vary run to run, so a change in score could no longer be
/// attributed to the system under test. A deterministic split keeps the predicate set a pure function of
/// the dataset.</para>
/// <para><b>Conservative on purpose.</b> Splitting only on sentence terminators, semicolons, newlines and
/// list markers under-splits: "black and white" stays one predicate, and so does a genuine two-fact
/// clause joined by "and". Under-splitting degrades toward today's single-judge behaviour, which is
/// merely no better. Over-splitting would manufacture fragments that no response can support, which
/// would invent failures — so where the two errors are not symmetric, this leans to the safe one.</para>
/// <para>A gold answer that yields one predicate costs exactly one judge call, the same as
/// <see cref="Models.JudgeDecompositionMode.None"/>.</para>
/// </remarks>
internal static class LongMemEvalPredicateExtractor
{
    /// <summary>
    /// Upper bound on predicates per question. Bounds worst-case provider spend: with the default
    /// retry budget a single question can otherwise fan out without limit on a verbose gold answer.
    /// </summary>
    internal const int MaximumPredicates = 8;

    /// <summary>Shortest fragment treated as a claim; below this it is punctuation noise, not a fact.</summary>
    internal const int MinimumPredicateLength = 12;

    private static readonly char[] s_terminators = ['.', '!', '?', ';', '\n', '\r'];

    /// <summary>
    /// Phrases that mark a gold answer as offering ALTERNATIVES rather than listing conjoined facts.
    /// </summary>
    /// <remarks>
    /// Decomposition assumes the gold answer is a conjunction — every claim must hold. LongMemEval
    /// temporal answers routinely are not: <c>"7 days. 8 days (including the last day) is also
    /// acceptable."</c> offers two mutually exclusive acceptable answers. Splitting that and requiring
    /// both would fail a response that gave the primary answer, manufacturing a wrong verdict out of a
    /// correct one. Such answers are judged whole.
    /// </remarks>
    private static readonly string[] s_alternativeMarkers =
    [
        "also acceptable",
        "is acceptable",
        "are acceptable",
        " or ",
    ];

    /// <summary>
    /// Extracts the ordered, de-duplicated predicate list for a gold answer. Never returns an empty list
    /// for a non-blank gold answer — it falls back to the whole answer as a single predicate, so a
    /// question is never silently dropped from scoring.
    /// </summary>
    /// <remarks>
    /// Decomposes only when the split is clearly safe. Any sign that it is not — alternative phrasing, or
    /// a fragment too short to be a standalone claim — falls back to judging the whole answer, which
    /// costs one call and reproduces single-judge behaviour. Under-decomposing is merely no better than
    /// today; mis-decomposing would invent failures.
    /// </remarks>
    internal static IReadOnlyList<string> Extract(string goldAnswer)
    {
        if (string.IsNullOrWhiteSpace(goldAnswer))
            return [];

        var trimmedAnswer = goldAnswer.Trim();

        if (OffersAlternatives(trimmedAnswer))
            return [trimmedAnswer];

        var fragments = trimmedAnswer
            .Split(s_terminators, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanFragment)
            .Where(f => f.Length > 0)
            .ToList();

        // Nothing to decompose, or the answer was one sentence.
        if (fragments.Count <= 1)
            return [trimmedAnswer];

        // A fragment too short to stand alone means the split cut something that was not a claim — a
        // decimal point, an abbreviation, or a terse primary answer such as "7 days". Dropping it would
        // silently change what the question requires, so the whole answer is judged instead.
        if (fragments.Any(f => f.Length < MinimumPredicateLength))
            return [trimmedAnswer];

        // A fragment ending in a digit means the terminator that produced it belonged to a number, not
        // to a sentence: an enumerated list ("...is: 1. Billie Eilish concert..., 2. Free outdoor...")
        // or a decimal ("$12.50"). Splitting there yields fragments with a dangling ordinal — asking
        // whether a response supports "Billie Eilish concert at the Wells Fargo Center in Philly, 2" is
        // not a question about the answer. Judge whole instead.
        if (fragments.Any(f => char.IsAsciiDigit(f[^1])))
            return [trimmedAnswer];

        var predicates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in fragments)
        {
            if (!seen.Add(fragment))
                continue;

            predicates.Add(fragment);
            if (predicates.Count == MaximumPredicates)
                break;
        }

        return predicates.Count > 0 ? predicates : [trimmedAnswer];
    }

    private static bool OffersAlternatives(string goldAnswer)
    {
        foreach (var marker in s_alternativeMarkers)
        {
            if (goldAnswer.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string CleanFragment(string fragment)
    {
        var text = fragment.Trim();

        // Strip leading list markers: "- ", "* ", "1. ", "2) ".
        var index = 0;
        while (index < text.Length && (text[index] == '-' || text[index] == '*' || text[index] == '•'))
            index++;
        while (index < text.Length && char.IsDigit(text[index]))
            index++;
        while (index < text.Length && (text[index] == '.' || text[index] == ')' || text[index] == ':'))
            index++;

        return index > 0 && index < text.Length ? text[index..].Trim() : text;
    }
}
