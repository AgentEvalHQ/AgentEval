namespace AgentEval.Results.Tests;

/// <summary>
/// [ENC-8]: an AEF time at its full precision (nine fraction digits), as read, compared and written (n2-d: the writer's
/// model holds <see cref="AefTime"/>, so a time is written as given).
/// </summary>
public class AefTimeTests
{
    [Theory]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T00:00:00.000Z", "2026-10-01T00:00:00Z")]                    // the shortest form
    [InlineData("2026-10-01T00:00:00.50Z", "2026-10-01T00:00:00.5Z")]
    [InlineData("2026-10-01T00:00:00.000000001Z", "2026-10-01T00:00:00.000000001Z")]    // finer than 100 ns
    [InlineData("2026-10-01T23:59:59.999999999Z", "2026-10-01T23:59:59.999999999Z")]
    [InlineData("1969-12-31T23:59:59.5Z", "1969-12-31T23:59:59.5Z")]                    // before the epoch
    [InlineData("0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.999999999Z", "9999-12-31T23:59:59.999999999Z")]
    public void ATimeIsWrittenAtTheFullPrecisionRead_InTheShortestForm(string text, string written)
    {
        var time = AefTime.Parse(text);

        Assert.True(time.IsValid);
        Assert.Equal(written, time.ToString());
        Assert.Equal(time, AefTime.Parse(time.ToString()));
    }

    [Fact]
    public void ADateTimeOffset_ConvertsExactly_WhateverItsOffset()
    {
        var utc = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var local = utc.ToOffset(TimeSpan.FromHours(-7.5));
        AefTime fromUtc = utc;

        Assert.Equal("2026-10-01T10:00:00.1234567Z", fromUtc.ToString());
        Assert.Equal(fromUtc, AefTime.FromDateTimeOffset(local));
        Assert.Equal(AefTime.Parse("1969-12-31T23:59:59.9999999Z"), AefTime.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddTicks(-1)));
        Assert.Equal(AefTime.Parse("0001-01-01T00:00:00Z"), AefTime.FromDateTimeOffset(DateTimeOffset.MinValue));
    }

    [Theory]
    [InlineData(0L, 1_000_000_000)]
    [InlineData(0L, -1)]
    [InlineData(-62_135_596_801L, 0)]      // the year 0000
    [InlineData(253_402_300_800L, 0)]      // the year 10000
    public void PartsThatAreNoAefTime_AreNotValid_AndAreNotWrittenAsOne(long seconds, int nanoseconds)
    {
        var time = new AefTime(seconds, nanoseconds);

        Assert.False(time.IsValid);
        Assert.Throws<FormatException>(() => AefTime.Parse(time.ToString()));
    }
}
