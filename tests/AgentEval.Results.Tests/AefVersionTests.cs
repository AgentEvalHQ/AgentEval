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

    [Theory]
    [InlineData("2.0", "2")]
    [InlineData("0.9", "0")]
    [InlineData("12.3", "12")]
    [InlineData("1.0", null)]
    [InlineData("1.7", null)]     // a later minor of this major: not another major
    [InlineData("2", null)]       // no MAJOR.MINOR: the schema's to refuse
    [InlineData("2.", null)]
    [InlineData(".0", null)]
    [InlineData("2.0.1", null)]
    [InlineData("v2.0", null)]
    [InlineData(null, null)]
    public void AnotherMajor_IsNamed_ForVer4sMessage(string? schemaVersion, string? major)
    {
        // [VER-4]: a reader refuses a major version it does not know, with a message that says so.
        Assert.Equal(major, AefVersion.OtherMajor(schemaVersion is null ? null : System.Text.Json.Nodes.JsonValue.Create(schemaVersion)));
    }

    [Fact]
    public void ACheckpointManifestOfAnotherMajor_IsRefusedWithAMessageThatSaysSo()
    {
        var refused = Assert.Throws<FormatException>(() => global::AgentEval.Results.Checkpoints.CheckpointVerifier.Read("""{"schemaVersion": "2.0"}"""u8));
        Assert.Contains("AEF major version 2", refused.Message, StringComparison.Ordinal);
    }
}
