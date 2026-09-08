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
/// <para>
/// ⚠ <b>THAT PARAGRAPH WAS FALSE AS FIRST SHIPPED, and the reason is worth keeping.</b> The
/// normalising step discounted ANY leading run of one to three letters followed by
/// <c>. ) : -</c> — which describes a Roman numeral and equally describes <c>Re-</c>, <c>AI-</c>,
/// <c>No:</c> and <c>Top-</c>. Measured on the shipped build:
/// <c>AreSameCriterion("Re-check the sources", "Check the sources")</c> returned
/// <see langword="true"/>, a similarity match made by the class that promises none; and
/// <c>MatchDeclared("1. Re-check the sources", ["Re-check the sources"])</c> returned
/// <see langword="null"/>, so the echo this type exists to rejoin did not rejoin. Both directions
/// failed at once, from one missing condition. See <see cref="StripLeadingEnumerator"/>.
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
    /// produces: <c>1.</c> <c>1)</c> <c>(1)</c> <c>a.</c> <c>A.</c> <c>iv.</c> <c>-</c> <c>*</c>
    /// <c>•</c>. Everything after it is left as text. <c>#1</c> is <b>not</b> one of them and never
    /// was: that form carries no separator, and the test suite has pinned it as UNSTRIPPED since
    /// this type shipped. The list above named it anyway, in both copies of it.
    /// </para>
    /// <para>
    /// ⚠ <b>THREE CONDITIONS, AND TWO OF THEM WERE MISSING AT FIRST SHIP.</b> A leading run is
    /// discounted only when it is <b>short</b> (one to three characters), <b>shaped like an
    /// enumerator</b> (all digits, one letter, or a Roman numeral) and <b>followed by a separator
    /// and then a space or the end of the text</b>. Without the last two,
    /// <c>Re-check the sources</c> normalised to <c>check the sources</c> and
    /// <c>AI-generated text is labelled</c> to <c>generated text is labelled</c> — a real leading
    /// word eaten because it happened to be short and hyphenated.
    /// </para>
    /// <para>
    /// Ported from the Galaxus Eval 05 repair of 2026-09-05 so the rule lives in one place instead
    /// of once per consumer — the sample's copy CALLS this method now rather than keeping a second
    /// one that could drift. It accepts upper-case labels as well as lower-case; on lower-cased
    /// input the two are identical.
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

        // A label has to be SHORT — "1", "12", "a", "iv" …
        if (labelLength is < 1 or > 3) return text;

        // … AND SHAPED LIKE ONE. "Re", "AI", "No", "Do" and "Top" are short WORDS, and
        // discounting a word is how a normaliser starts inventing matches.
        if (!IsEnumeratorLabel(text.AsSpan(labelStart, labelLength))) return text;

        int afterLabel = i;
        while (i < text.Length && (text[i] == '.' || text[i] == ')' || text[i] == ':' || text[i] == '-')) i++;
        if (i == afterLabel) return text;                    // no separator: not an enumeration

        // … AND THE MARKER HAS TO END. A rendered list puts a space after it; a hyphenated word
        // does not, which is what leaves "iv-league" and "i.e. something" as text.
        if (i < text.Length && text[i] != ' ') return text;

        while (i < text.Length && text[i] == ' ') i++;

        return i >= text.Length ? text : text[i..];
    }

    /// <summary>
    /// Whether a short leading run is shaped like an enumeration label: all digits (<c>1</c>,
    /// <c>12</c>), one letter (<c>a</c>, <c>A</c>), or a Roman numeral (<c>iv</c>, <c>III</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ The point of it is what it REFUSES. Every two- or three-letter word that is not a Roman
    /// numeral — <c>Re</c>, <c>AI</c>, <c>No</c>, <c>Do</c>, <c>Top</c> — fails here, so a criterion
    /// that begins with one keeps its first word.
    /// </remarks>
    /// <param name="label">The candidate label, one to three characters.</param>
    /// <returns>True when the run could be an enumeration label.</returns>
    private static bool IsEnumeratorLabel(ReadOnlySpan<char> label)
    {
        bool allDigits = true;
        bool allRoman = true;
        foreach (char c in label)
        {
            if (!char.IsAsciiDigit(c)) allDigits = false;
            if ("ivxlcdmIVXLCDM".IndexOf(c) < 0) allRoman = false;
        }
        return allDigits || allRoman || (label.Length == 1 && char.IsAsciiLetter(label[0]));
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
