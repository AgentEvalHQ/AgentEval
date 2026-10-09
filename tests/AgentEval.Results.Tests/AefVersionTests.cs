namespace AgentEval.Results.Tests;

/// <summary>
/// [VER-1], [CKP-8]: what a <c>schemaVersion</c> declares against 1.0. A minor is written without a leading zero and
/// compared as the number it reads (R5N-4); only a later one can make a lane <c>unverifiable</c>.
/// </summary>
public class AefVersionTests
{
    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.1", true)]
    [InlineData("1.10", true)]
    [InlineData("1.123", true)]
    [InlineData("1.00", false)]   // no version: the reader schema refuses a leading zero
    [InlineData("1.01", false)]
    [InlineData("2.0", false)]
    [InlineData("1.", false)]
    [InlineData("1", false)]
    [InlineData("1.1a", false)]
    [InlineData(null, false)]
    public void ALaterMinor_IsOneAbove0_WrittenWithoutALeadingZero(string? schemaVersion, bool later)
    {
        Assert.Equal(later, AefVersion.DeclaresLaterMinor(schemaVersion));
    }
}
