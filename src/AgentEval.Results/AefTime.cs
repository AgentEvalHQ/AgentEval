// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.Results;

/// <summary>
/// An AEF time (RFC 3339 in UTC, ending in Z, up to nine fraction digits) at its full precision: seconds since the Unix
/// epoch and nanoseconds. DateTimeOffset keeps 100 ns, so 12:00:00.000000001Z would round to 12:00:00Z; comparisons the
/// format defines (freshness) need the exact value.
/// </summary>
public readonly record struct AefTime(long Seconds, int Nanoseconds) : IComparable<AefTime>
{
    private static readonly Regex Pattern = new(
        "^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.([0-9]{1,9}))?Z\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Parses an AEF time.</summary>
    /// <exception cref="FormatException">Anything that is not one (another offset, a space instead of T, ten fraction digits).</exception>
    public static AefTime Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = Pattern.Match(text);
        if (!m.Success)
        {
            throw new FormatException($"'{text}' is not an RFC 3339 UTC time ending in Z.");
        }

        int Part(int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        DateTimeOffset whole;
        try
        {
            whole = new DateTimeOffset(Part(1), Part(2), Part(3), Part(4), Part(5), Part(6), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The pattern allows day 31 in any month: an impossible date is refused, never rolled over.
            throw new FormatException($"'{text}' is not a date that exists.");
        }

        var nanos = m.Groups[7].Success ? int.Parse(m.Groups[7].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0;
        return new AefTime(whole.ToUnixTimeSeconds(), nanos);
    }

    /// <summary>This time plus a whole number of seconds.</summary>
    public AefTime AddSeconds(long seconds) => this with { Seconds = Seconds + seconds };

    /// <inheritdoc />
    public int CompareTo(AefTime other) =>
        Seconds != other.Seconds ? Seconds.CompareTo(other.Seconds) : Nanoseconds.CompareTo(other.Nanoseconds);

    /// <summary>Earlier than.</summary>
    public static bool operator <(AefTime a, AefTime b) => a.CompareTo(b) < 0;

    /// <summary>Later than.</summary>
    public static bool operator >(AefTime a, AefTime b) => a.CompareTo(b) > 0;

    /// <summary>Not later than.</summary>
    public static bool operator <=(AefTime a, AefTime b) => a.CompareTo(b) <= 0;

    /// <summary>Not earlier than.</summary>
    public static bool operator >=(AefTime a, AefTime b) => a.CompareTo(b) >= 0;
}
