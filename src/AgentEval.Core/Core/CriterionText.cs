// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// One shared rule for recognising a judge's echo of a criterion we ourselves rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="ChatClientEvaluator"/> renders the rubric into the judge
/// prompt as <c>string.Join("\n", criteria.Select((c, i) =&gt; $"{i + 1}. {c}"))</c> — it prepends
/// the ordinal itself. A faithful judge echoes back exactly what it was shown, so
/// <c>CriterionResult.Criterion</c> arrives as <c>"1. Every recommendation is tied to…"</c> where
/// the rubric holds <c>"Every recommendation is tied to…"</c>. <b>A three-character offset defeats
/// exact matching, whitespace-normalised matching and prefix matching alike</b>, and every consumer
/// that joins a verdict back to the criterion it answers inherits the hazard.
/// </para>
/// <para>
/// <b>It has bitten twice in this repository, in the unsafe direction both times.</b> The Galaxus
/// Eval 05 run of 2026-09-05 recorded 24 lines reading <i>"the judge returned a criterion nobody
/// declared"</i> on 3 of 10 judged cells — every one of them one of the eval's own five declared
/// criteria carrying our own ordinal. And
/// <c>CalibratedEvaluator.AggregateCriteriaResults</c> matches judge verdicts to criteria with an
/// ordinal-ignore-case <c>string.Equals</c>: an echoed ordinal drops the verdict and the criterion
/// is aggregated as <c>Met = false</c> with <i>"No judges returned a result for this criterion."</i>
/// <b>A met criterion becomes an unmet one</b> — the flattering-to-nobody direction, and silent.
/// </para>
/// <para>
/// <b>This is UN-RENDERING OUR OWN PROMPT, not guessing.</b> Nothing here matches by position and
/// nothing here matches by similarity. <see cref="RealignToDeclared"/> rewrites a criterion only
/// when its normalised form is <i>equal</i> to the normalised form of exactly one declared
/// criterion. A criterion the judge genuinely invented does not match anything, is left exactly as
/// the judge wrote it, and stays visible to the consumer that wants to report it.
/// </para>
/// </remarks>
public static class CriterionText
{
    /// <summary>
    /// Removes ONE leading enumeration marker from <paramref name="text"/>, or returns it unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately narrow.</b> One marker, only at the start, only the forms a list renderer
    /// produces: <c>1.</c> <c>1)</c> <c>(1)</c> <c>a.</c> <c>A.</c> <c>iv.</c> <c>#1</c> <c>-</c>
    /// <c>*</c> <c>•</c>. Everything after it is left as text. A label longer than three characters
    /// is a word, and stripping a word is how a normaliser starts inventing matches.
    /// </para>
    /// <para>
    /// Ported from the Galaxus Eval 05 repair of 2026-09-05 so the rule lives in one place instead
    /// of once per consumer. It accepts upper-case labels as well as lower-case, which the sample's
    /// copy could not see because it lower-cased first; on lower-cased input the two are identical.
    /// </para>
    /// </remarks>
    /// <param name="text">The text to strip. Not null.</param>
    /// <returns>The text without its leading enumeration marker, or the input unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string StripLeadingEnumerator(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int i = 0;
        if (i < text.Length && (text[i] == '(' || text[i] == '#')) i++;

        int labelStart = i;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || char.IsAsciiLetter(text[i]))) i++;
        int labelLength = i - labelStart;

        // A bullet: no label at all, just the mark and a space.
        if (labelLength == 0 && labelStart == 0 && text.Length > 1
            && (text[0] == '-' || text[0] == '*' || text[0] == '•')
            && text[1] == ' ')
        {
            return text[2..];
        }

        // A label has to be SHORT — "1", "12", "a", "iv".
        if (labelLength is < 1 or > 3) return text;

        while (i < text.Length && (text[i] == '.' || text[i] == ')' || text[i] == ':' || text[i] == '-')) i++;
        if (i == labelStart + labelLength) return text;      // no separator: not an enumeration
        while (i < text.Length && text[i] == ' ') i++;

        return i >= text.Length ? text : text[i..];
    }

    /// <summary>
    /// The comparison form of a criterion: whitespace collapsed, lower-cased, then ONE leading
    /// enumeration marker removed.
    /// </summary>
    /// <param name="text">The criterion text, or null.</param>
    /// <returns>The normalised form. Empty for null, empty or whitespace-only input.</returns>
    public static string Normalize(string? text) =>
        text is null
            ? string.Empty
            : StripLeadingEnumerator(
                string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                      .ToLowerInvariant());

    /// <summary>
    /// True when two criterion strings name the same criterion once the enumeration marker our own
    /// renderer prepends is discounted.
    /// </summary>
    /// <remarks>
    /// Two empty or whitespace-only strings are <b>not</b> the same criterion: an empty criterion
    /// carries no claim, and treating two of them as equal is how a join starts matching absences
    /// to each other.
    /// </remarks>
    /// <param name="a">One criterion.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when both normalise to the same non-empty string.</returns>
    public static bool AreSameCriterion(string? a, string? b)
    {
        var na = Normalize(a);
        return na.Length != 0 && string.Equals(na, Normalize(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the one declared criterion <paramref name="echoed"/> names, or null.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing matches AND when more than one declared criterion matches. An
    /// ambiguous rubric (two criteria that differ only by an enumeration marker) is a rubric
    /// defect; resolving it by picking the first would hide it.
    /// </remarks>
    /// <param name="echoed">What the judge returned.</param>
    /// <param name="declared">The criteria we asked about.</param>
    /// <returns>The declared criterion, verbatim, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="declared"/> is null.</exception>
    public static string? MatchDeclared(string? echoed, IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var needle = Normalize(echoed);
        if (needle.Length == 0) return null;

        string? found = null;
        foreach (var candidate in declared)
        {
            if (!string.Equals(Normalize(candidate), needle, StringComparison.Ordinal)) continue;
            if (found is not null && !string.Equals(found, candidate, StringComparison.Ordinal)) return null;
            found ??= candidate;
        }
        return found;
    }

    /// <summary>
    /// Re-anchors each verdict's <see cref="CriterionResult.Criterion"/> to the declared criterion
    /// it names, leaving anything that names none of them exactly as the judge wrote it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned list has the same length and the same order as
    /// <paramref name="results"/>; <see cref="CriterionResult.Met"/> and
    /// <see cref="CriterionResult.Explanation"/> are carried across untouched. A verdict whose
    /// criterion already equals a declared one is returned as the same instance.
    /// </para>
    /// <para>
    /// ⚠ <b>This is the one place a criterion string is rewritten.</b> It fires only on equality of
    /// the normalised forms, so it can add a join that our own rendering broke and it can never add
    /// a join between two different criteria.
    /// </para>
    /// </remarks>
    /// <param name="results">The judge's verdicts, in the order it returned them.</param>
    /// <param name="declared">The criteria we asked about.</param>
    /// <returns>The re-anchored verdicts.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<CriterionResult> RealignToDeclared(
        IEnumerable<CriterionResult> results,
        IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(declared);

        var declaredList = declared as IReadOnlyList<string> ?? [.. declared];
        var realigned = new List<CriterionResult>();

        foreach (var result in results)
        {
            if (result is null) continue;

            // Already verbatim-equal to something we asked about: nothing to do, and saying so
            // costs one comparison rather than a normalise per declared criterion.
            bool verbatim = false;
            foreach (var candidate in declaredList)
            {
                if (!string.Equals(candidate, result.Criterion, StringComparison.Ordinal)) continue;
                verbatim = true;
                break;
            }

            if (verbatim)
            {
                realigned.Add(result);
                continue;
            }

            var match = MatchDeclared(result.Criterion, declaredList);
            realigned.Add(match is null
                ? result
                : new CriterionResult
                {
                    Criterion = match,
                    Met = result.Met,
                    Explanation = result.Explanation,
                });
        }

        return realigned;
    }
}
