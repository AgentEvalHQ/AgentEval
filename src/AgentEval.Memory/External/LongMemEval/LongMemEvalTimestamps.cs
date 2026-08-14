// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Parses the corpus's date strings into instants a memory system can store.
/// </summary>
/// <remarks>
/// <para>
/// LongMemEval writes dates as <c>2023/05/20 (Sat) 02:21</c>. The parenthesised day name is
/// redundant with the date and is dropped on the fallback path, so an entry whose day name
/// disagrees with its date still parses rather than failing the run over a cosmetic inconsistency.
/// </para>
/// <para>
/// The corpus carries no time zone, so values are read as UTC. That is an assumption, and it is
/// stated rather than hidden: every comparison a temporal question makes is between two values read
/// under the same assumption, so the arithmetic is unaffected, but an absolute instant from here is
/// not evidence about a real-world local time.
/// </para>
/// </remarks>
public static partial class LongMemEvalTimestamps
{
    private static readonly string[] ExactFormats =
    [
        "yyyy/MM/dd (ddd) HH:mm",
        "yyyy/MM/dd (dddd) HH:mm",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm",
        "yyyy/MM/dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd"
    ];

    [GeneratedRegex(@"\s*\([^)]*\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DayNameSuffix();

    /// <summary>
    /// Parses a corpus date into a UTC instant, or returns null when the value is not a date this
    /// corpus uses. Null is never substituted with a default: an invented timestamp would be
    /// indistinguishable from a real one to everything downstream.
    /// </summary>
    public static DateTimeOffset? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (TryParseCore(text) is { } parsed)
            return parsed;

        // Second chance without the day name, so "2023/05/20 (Sun) 02:21" on a Saturday still reads
        // as the 20th of May rather than failing the whole run.
        var withoutDayName = DayNameSuffix().Replace(text, " ").Trim();
        return withoutDayName.Length == text.Length ? null : TryParseCore(withoutDayName);
    }

    private static DateTimeOffset? TryParseCore(string text)
    {
        if (DateTime.TryParseExact(
                text,
                ExactFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            return new DateTimeOffset(exact, TimeSpan.Zero);
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var loose)
            ? loose
            : null;
    }

    /// <summary>
    /// Parses a value that a time-grounded run requires, throwing rather than degrading. Under
    /// grounding, a session AgentEval cannot place in time is a session the agent cannot be asked to
    /// place in time either, and continuing would score a question nothing could answer.
    /// </summary>
    internal static DateTimeOffset Require(string? value, string questionId, string field)
        => TryParse(value)
            ?? throw new LongMemEvalTemporalGroundingException(questionId, field, value);

    /// <summary>
    /// True when the text contains an explicit numeric date or a four-digit year. Deliberately
    /// conservative: it is used to report what in-text date removal could <i>not</i> remove, and an
    /// over-eager pattern would overstate the problem.
    /// </summary>
    internal static bool LooksDated(string? text)
        => !string.IsNullOrEmpty(text) && DateLikeContent().IsMatch(text);

    [GeneratedRegex(
        @"\b(19|20)\d{2}\b|\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b|\b\d{4}[/-]\d{1,2}[/-]\d{1,2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex DateLikeContent();
}

/// <summary>
/// Thrown when a run asked for time-grounded evaluation and the corpus carries a date the harness
/// cannot turn into an instant.
/// </summary>
/// <remarks>
/// It fails the run before any provider call rather than substituting a placeholder. A placeholder
/// would make the affected questions unanswerable by construction while still scoring them, which is
/// the failure mode time-grounding exists to expose.
/// </remarks>
public sealed class LongMemEvalTemporalGroundingException : InvalidOperationException
{
    /// <summary>Question whose date could not be parsed.</summary>
    public string QuestionId { get; }

    /// <summary>Which field carried it (a session date or the question date).</summary>
    public string Field { get; }

    /// <summary>The unparseable value, as the corpus wrote it.</summary>
    public string? Value { get; }

    internal LongMemEvalTemporalGroundingException(string questionId, string field, string? value)
        : base(
            $"Time-grounded evaluation requires a parseable date for every session, but question " +
            $"'{questionId}' has {field} = '{value ?? "<null>"}', which is not a date this corpus " +
            $"format uses. Fix the entry, or run with TemporalGroundingMode.None.")
    {
        QuestionId = questionId;
        Field = field;
        Value = value;
    }
}
