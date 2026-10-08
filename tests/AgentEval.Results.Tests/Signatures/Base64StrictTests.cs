using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>Base64 as [SIG-1] writes and reads it.</summary>
public class Base64StrictTests
{
    [Fact]
    public void Encode_WritesTheStandardAlphabet_WithPadding()
    {
        Assert.Equal("+/8=", Base64Strict.Encode([0xFB, 0xFF]));
        Assert.Equal("QQ==", Base64Strict.Encode("A"u8));
        Assert.Equal("", Base64Strict.Encode([]));
    }

    [Theory]
    [InlineData("+/8=", "FBFF")]   // standard, padded
    [InlineData("+/8", "FBFF")]    // standard, unpadded
    [InlineData("-_8=", "FBFF")]   // URL-safe, padded
    [InlineData("-_8", "FBFF")]    // URL-safe, unpadded
    [InlineData("QQ==", "41")]
    [InlineData("QQ", "41")]
    [InlineData("QUI=", "4142")]
    [InlineData("QUI", "4142")]
    [InlineData("QUJD", "414243")]
    [InlineData("", "")]
    public void TryDecode_ReadsEitherAlphabet_WithOrWithoutPadding(string text, string hex)
    {
        Assert.True(Base64Strict.TryDecode(text, out var bytes));
        Assert.Equal(hex, Convert.ToHexString(bytes));
    }

    [Theory]
    // whitespace, anywhere
    [InlineData(" QQ==")]
    [InlineData("QQ ==")]
    [InlineData("QQ==\n")]
    [InlineData("QU\r\nJD")]
    [InlineData("\tQUJD")]
    // both alphabets in one text
    [InlineData("+-AA")]
    [InlineData("/_AA")]
    [InlineData("ab+_")]
    // set unused bits: a canonical encoder leaves them zero
    [InlineData("QR==")]
    [InlineData("QR")]
    [InlineData("QUJ=")]
    [InlineData("QUJ")]
    // a length no base64 text has
    [InlineData("Q")]
    [InlineData("QUJDR")]
    // padding that does not fit the length
    [InlineData("QQ=")]
    [InlineData("QQ===")]
    [InlineData("QUI==")]
    [InlineData("QUJD=")]
    [InlineData("QUJD====")]
    [InlineData("=")]
    [InlineData("==")]
    [InlineData("Q=Q=")]
    // outside the alphabet
    [InlineData("QQ!=")]
    [InlineData("QQ..")]
    [InlineData("QUJÉ")]
    public void TryDecode_Refuses(string text)
    {
        Assert.False(Base64Strict.TryDecode(text, out var bytes));
        Assert.Null(bytes);
        Assert.Throws<FormatException>(() => Base64Strict.Decode(text));
    }

    [Fact]
    public void TryDecode_RefusesNull() => Assert.False(Base64Strict.TryDecode(null, out _));

    [Fact]
    public void EveryLength_RoundTrips_InBothAlphabets_PaddedOrNot()
    {
        var random = new Random(8032);
        for (var length = 0; length < 40; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            var standard = Base64Strict.Encode(bytes);
            Assert.Equal(Convert.ToBase64String(bytes), standard);

            var urlSafe = standard.Replace('+', '-').Replace('/', '_');
            foreach (var text in new[] { standard, standard.TrimEnd('='), urlSafe, urlSafe.TrimEnd('=') })
            {
                Assert.True(Base64Strict.TryDecode(text, out var decoded), text);
                Assert.Equal(bytes, decoded);
            }
        }
    }
}
