using System.Text;
using AgentEval.Results.Json;

namespace AgentEval.Results.Tests.Json;

/// <summary>[ENC-5]–[ENC-7]: NDJSON framing, and each line read on its own.</summary>
public class AefNdjsonTests
{
    private static AefNdjsonFile Read(string text) => AefNdjson.Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AnEmptyFile_IsValidAndHoldsNoLines()
    {
        var file = Read("");

        Assert.True(file.IsValid);
        Assert.Empty(file.Lines);
    }

    [Fact]
    public void Lines_AreNumberedFromOne_WithTheirOffsetAndLength()
    {
        var file = Read("{\"a\":1}\n{\"b\":22}\n");

        Assert.True(file.IsValid);
        Assert.Collection(file.Lines,
            l => Assert.Equal((1, 0, 7), (l.Number, l.Offset, l.Length)),
            l => Assert.Equal((2, 8, 8), (l.Number, l.Offset, l.Length)));
        Assert.Equal(22, (int)file.Lines[1].Value!["b"]!);
    }

    [Theory]
    [InlineData("{\"a\":1}\r\n")]                 // CRLF
    [InlineData("{\"a\":1}\n\n{\"b\":2}\n")]       // a blank line
    [InlineData("\n{\"a\":1}\n")]                  // a blank first line
    [InlineData("{\"a\":1}\n{\"b\":2}")]           // the last line without LF: incomplete
    [InlineData("{\"a\":\"x\ry\"}\n")]            // a CR anywhere, even where JSON would refuse it anyway
    public void BrokenFraming_IsOneProblemWithTheFile_AndNoLineIsRead(string text)
    {
        var file = Read(text);

        Assert.Equal("encoding", file.Problem!.Code);
        Assert.Empty(file.Lines);
    }

    [Fact]
    public void AByteOrderMark_IsAFramingProblem()
    {
        var file = AefNdjson.Read([0xEF, 0xBB, 0xBF, .. "{\"a\":1}\n"u8]);

        Assert.Equal("encoding", file.Problem!.Code);
        Assert.Empty(file.Lines);
    }

    [Fact]
    public void U2028AndU2029InAString_AreNotLineBreaks()
    {
        var file = Read($"{{\"s\":\"a{(char)0x2028}b{(char)0x2029}c\"}}\n");

        Assert.True(file.IsValid);
        Assert.Single(file.Lines);
    }

    [Fact]
    public void ALineThatIsNotAnIJsonObject_IsAProblemOfThatLineOnly()
    {
        var file = Read("{\"a\":1}\n[1]\n{\"a\":1,\"a\":2}\nnot json\n{\"ok\":true}\n");

        Assert.Null(file.Problem);
        Assert.False(file.IsValid);
        Assert.Equal(new string?[] { null, "encoding", "encoding", "encoding", null }, file.Lines.Select(l => l.Problem?.Code));
        Assert.NotNull(file.Lines[4].Value);
    }

    [Fact]
    public void ALineNestedDeeperThan64_IsALimitOfThatLine()
    {
        var deep = "{\"x\":" + new string('[', 64) + new string(']', 64) + "}";
        var file = Read($"{{}}\n{deep}\n");

        Assert.Equal(new string?[] { null, "limit" }, file.Lines.Select(l => l.Problem?.Code));
    }

    [Fact]
    public void MoreLinesThanTheLimit_IsALimitOfTheFile()
    {
        var bytes = new byte[(AefLimits.MaxLines + 1) * 3];
        for (var i = 0; i < bytes.Length; i += 3)
        {
            bytes[i] = (byte)'{';
            bytes[i + 1] = (byte)'}';
            bytes[i + 2] = (byte)'\n';
        }

        Assert.Equal("limit", AefNdjson.Read(bytes).Problem!.Code);
        Assert.True(AefNdjson.Read(bytes.AsSpan(0, AefLimits.MaxLines * 3)).IsValid);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("{}", 0)]
    [InlineData("{}\n{\"a\"", 3)]
    [InlineData("{}\n{}\n", 6)]
    public void CompleteLength_StopsAfterTheLastLf(string text, int length)
    {
        Assert.Equal(length, AefNdjson.CompleteLength(Encoding.UTF8.GetBytes(text)));
    }
}
