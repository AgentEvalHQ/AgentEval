// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.Results;

/// <summary>
/// An AEF time (RFC 3339 in UTC, ending in Z, up to nine fraction digits, in the years 0001 to 9999, [ENC-8]) at its
/// full precision: seconds since the Unix epoch and nanoseconds (0 to 999,999,999). DateTimeOffset keeps 100 ns, so
/// 12:00:00.000000001Z would round to 12:00:00Z; comparisons the format defines (freshness) need the exact value, and a
/// writer writes a time given with nine fraction digits as given (n2-d). A <see cref="DateTimeOffset"/> converts to one
/// exactly; <see cref="ToString"/> writes the [ENC-8] text.
/// </summary>
public readonly record struct AefTime(long Seconds, int Nanoseconds) : IComparable<AefTime>
{
    private static readonly Regex Pattern = new(
        "^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.([0-9]{1,9}))?Z\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // 0001-01-01T00:00:00Z and 9999-12-31T23:59:59Z, in seconds since the Unix epoch: [ENC-8]'s years.
    private static readonly long MinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Whether this is an [ENC-8] time: nanoseconds from 0 to 999,999,999 and a second in the years 0001 to 9999 (a value
    /// built from its parts may be neither; <see cref="Parse"/> and a <see cref="DateTimeOffset"/> always give one).
    /// </summary>
    public bool IsValid => Nanoseconds is >= 0 and <= 999_999_999 && Seconds >= MinSeconds && Seconds <= MaxSeconds;

    /// <summary>The same instant, exactly (a <see cref="DateTimeOffset"/> holds 100 ns, which nanoseconds hold too).</summary>
    public static AefTime FromDateTimeOffset(DateTimeOffset time)
    {
        var utc = time.UtcTicks;
        return new AefTime(
            (utc / TimeSpan.TicksPerSecond) - (DateTimeOffset.UnixEpoch.UtcTicks / TimeSpan.TicksPerSecond),
            (int)(utc % TimeSpan.TicksPerSecond * 100));
    }

    /// <summary>A <see cref="DateTimeOffset"/> as an AEF time, exactly.</summary>
    public static implicit operator AefTime(DateTimeOffset time) => FromDateTimeOffset(time);

    /// <summary>
    /// The time as AEF writes it ([ENC-8]): UTC, <c>yyyy-MM-ddTHH:mm:ss</c>, the fraction of the second only when it is
    /// not zero and without trailing zeros (up to nine digits), then <c>Z</c>. A value that is not an AEF time
    /// (<see cref="IsValid"/>) is shown by its parts, which no reader reads as a time.
    /// </summary>
    public override string ToString()
    {
        if (!IsValid)
        {
            return $"AefTime {{ Seconds = {Seconds.ToString(CultureInfo.InvariantCulture)}, Nanoseconds = {Nanoseconds.ToString(CultureInfo.InvariantCulture)} }}";
        }

        var text = DateTimeOffset.FromUnixTimeSeconds(Seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return Nanoseconds == 0
            ? text + "Z"
            : $"{text}.{Nanoseconds.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0')}Z";
    }

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
        if (Part(1) == 0)
        {
            // [ENC-8]: the years 0001 to 9999 (RFC 3339 allows 0000, which date libraries disagree on).
            throw new FormatException($"'{text}' is in the year 0000: AEF times are in the years 0001 to 9999.");
        }

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
