using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Tests.Json;

/// <summary>[ENC-1]–[ENC-4] and [ENC-17]/[ENC-18] as <see cref="AefJsonReader"/> applies them.</summary>
public class AefJsonReaderTests
{
    private static JsonObject Parse(string text) => AefJsonReader.ParseDocument(Encoding.UTF8.GetBytes(text));

    private static string Code(byte[] bytes)
    {
        var e = Assert.ThrowsAny<AefReadException>(() => AefJsonReader.ParseDocument(bytes));
        return e.Code;
    }

    private static string Code(string text) => Code(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AnObject_IsRead()
    {
        var doc = Parse("""{"a": 1, "b": [true, null, "x"], "c": {"d": 2.5}}""");

        Assert.Equal(1, (int)doc["a"]!);
        Assert.Equal(2.5, (double)doc["c"]!["d"]!);
        Assert.Equal("x", (string)doc["b"]![2]!);
    }

    [Theory]
    [InlineData("""{"a": 1, "a": 2}""")]
    [InlineData("""{"a": 1, "\u0061": 2}""")]      // the same name after unescaping
    [InlineData("""{"\u00e9": 1, "é": 2}""")]
    [InlineData("""{"x": {"a": 1, "b": {"a": 3}, "a": 2}}""")]
    public void AMemberNamedTwice_IsAnEncodingProblem(string text)
    {
        Assert.Equal("encoding", Code(text));
    }

    [Fact]
    public void TheSameNameInTwoObjects_IsFine()
    {
        var doc = Parse("""{"a": {"a": 1}, "b": [{"a": 1}, {"a": 2}]}""");

        Assert.Equal(2, (int)doc["b"]![1]!["a"]!);
    }

    [Theory]
    [InlineData("""{"a": "\ud800"}""")]                 // a high surrogate alone
    [InlineData("""{"a": "\udc00x"}""")]                // a low surrogate alone
    [InlineData("""{"a": "\udc00\ud800"}""")]           // the pair reversed
    [InlineData("""{"a": "\ud800\u0041"}""")]      // a high surrogate before a non-surrogate
    [InlineData("""{"\ud800": 1}""")]                   // in a member name
    public void AnUnpairedSurrogate_IsAnEncodingProblem(string text)
    {
        Assert.Equal("encoding", Code(text));
    }

    [Fact]
    public void AnEscapedSurrogatePair_IsText()
    {
        Assert.Equal("\U0001D11E", (string)Parse("""{"a": "\ud834\udd1e"}""")["a"]!);
    }

    [Fact]
    public void AByteOrderMark_IsAnEncodingProblem()
    {
        Assert.Equal("encoding", Code([0xEF, 0xBB, 0xBF, .. "{}"u8]));
    }

    [Theory]
    [InlineData(new byte[] { 0xC0, 0xAF })]             // an overlong '/'
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]       // an encoded surrogate (CESU-8)
    [InlineData(new byte[] { 0xE2, 0x82 })]             // a truncated sequence
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // beyond U+10FFFF
    public void BytesThatAreNotUtf8_AreAnEncodingProblem(byte[] inString)
    {
        Assert.Equal("encoding", Code([.. "{\"a\": \""u8, .. inString, .. "\"}"u8]));
    }

    [Theory]
    [InlineData("""{"a": 1e400}""")]
    [InlineData("""{"a": -1e400}""")]
    [InlineData("""{"a": NaN}""")]
    [InlineData("""{"a": Infinity}""")]
    [InlineData("""{"a": -Infinity}""")]
    public void ANumberThatIsNotAFiniteBinary64Value_IsAnEncodingProblem(string text)
    {
        Assert.Equal("encoding", Code(text));
    }

    [Fact]
    public void ANumberWithTooManyDigitsToBeFinite_IsAnEncodingProblem()
    {
        Assert.Equal("encoding", Code($$"""{"a": 1{{new string('0', 400)}}}"""));
    }

    [Fact]
    public void ANumberThatUnderflows_ReadsAsZero()
    {
        Assert.Equal(0.0, (double)Parse("""{"a": 1e-400}""")["a"]!);
    }

    [Theory]
    [InlineData("""{"n": 2}""")]
    [InlineData("""{"n": 2.0}""")]
    [InlineData("""{"n": 2e0}""")]
    public void AnIntegerWrittenAnotherWay_ReadsAsTheSameBinary64Value(string text)
    {
        Assert.Equal(2.0, (double)Parse(text)["n"]!);
    }

    [Theory]
    [InlineData("""{"a": 1,}""")]                      // a trailing comma
    [InlineData("""{"a": 1} // note""")]               // a comment
    [InlineData("""{"a": 1} {"b": 2}""")]              // two values
    [InlineData("""{'a': 1}""")]
    [InlineData("""{"a": 01}""")]
    [InlineData("""{"a": "\x"}""")]
    [InlineData("{\"a\": \"line\nbreak\"}")]          // a raw control character in a string
    [InlineData("")]
    [InlineData("{")]
    public void NotAJsonText_IsAnEncodingProblem(string text)
    {
        Assert.Equal("encoding", Code(text));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void ATopLevelValueThatIsNotAnObject_IsNotADocument(string text)
    {
        Assert.Equal("encoding", Code(text));
        _ = AefJsonReader.ParseValue(Encoding.UTF8.GetBytes(text));   // but it is a JSON value
    }

    [Fact]
    public void U2028AndU2029_AreText()
    {
        var text = $"a{(char)0x2028}b{(char)0x2029}c";

        Assert.Equal(text, (string)Parse($"{{\"s\": \"{text}\"}}")["s"]!);
    }

    public static TheoryData<string> Nesting() => new() { "{\"a\":", "[" };

    [Theory]
    [MemberData(nameof(Nesting))]
    public void Depth64_IsRead_AndDepth65_IsALimit(string open)
    {
        static string Nested(string open, int depth)
        {
            // The top-level object is depth 1; each further object or array adds one.
            var close = open == "[" ? "]" : "}";
            return "{\"x\":" + string.Concat(Enumerable.Repeat(open, depth - 1)) + "1"
                   + string.Concat(Enumerable.Repeat(close, depth - 1)) + "}";
        }

        _ = Parse(Nested(open, 64));
        Assert.Equal("limit", Code(Nested(open, 65)));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2,\"d\":__DEEP__}")]     // a member named twice before the depth
    [InlineData("{\"d\":__DEEP__,\"x\":NaN}")]          // not a number after it
    [InlineData("﻿{\"d\":__DEEP__}")]              // a byte-order mark
    public void TheDepthIsCheckedOnTheBytesFirst_SoATextTooDeepAndNotIJson_IsALimit(string text)
    {
        // [ENC-17]: "checked on the bytes before the content is trusted": the answer does not depend on which defect a
        // parser meets first.
        var deep = string.Concat(Enumerable.Repeat("[", 64)) + string.Concat(Enumerable.Repeat("]", 64));
        Assert.Equal("limit", Code(text.Replace("__DEEP__", deep, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("{}", 1)]
    [InlineData("{\"a\":[]}", 2)]
    [InlineData("{\"a\":\"[[[{{{\"}", 1)]                 // brackets inside a string do not count
    [InlineData("{\"a\":\"\\\"[[[\"}", 1)]               // nor after an escaped quote
    [InlineData("[[]][[[]]]", 3)]
    public void DepthOf_ScansTheBytes(string text, int depth) =>
        Assert.Equal(depth, AefJsonReader.DepthOf(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void DeepNestingOfEmptyArrays_IsALimitNotAStackOverflow()
    {
        Assert.Equal("limit", Code("{\"x\":" + new string('[', 100_000)));
    }

    [Fact]
    public void FourMebibytes_IsRead_AndOneByteMore_IsALimit()
    {
        static byte[] OfSize(int size)
        {
            var bytes = Encoding.UTF8.GetBytes("{\"s\":\"" + new string('a', size - 8) + "\"}");
            Assert.Equal(size, bytes.Length);
            return bytes;
        }

        _ = AefJsonReader.ParseDocument(OfSize(AefLimits.MaxJsonBytes));
        Assert.Equal("limit", Code(OfSize(AefLimits.MaxJsonBytes + 1)));
    }

    [Fact]
    public void ASealMayBeUpTo40Mebibytes_AndAnEnvelopeUpTo56()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"s\":\"" + new string('a', AefLimits.MaxJsonBytes) + "\"}");

        Assert.Equal("limit", Code(bytes));
        _ = AefJsonReader.ParseDocument(bytes, AefLimits.MaxSealBytes);
        Assert.Equal("limit", Assert.ThrowsAny<AefReadException>(() => AefJsonReader.ParseDocument(bytes, 100)).Code);
    }

    [Theory]
    [InlineData("seal.json", AefLimits.MaxSealBytes)]
    [InlineData("runs/r1/seal.json", AefLimits.MaxSealBytes)]
    [InlineData("overlays/seal-0001.json", AefLimits.MaxSealBytes)]
    [InlineData("run/overlays/seal-0012.json", AefLimits.MaxSealBytes)]
    [InlineData("attestation.dsse.json", AefLimits.MaxEnvelopeBytes)]
    [InlineData("overlays/seal-0001.dsse.json", AefLimits.MaxEnvelopeBytes)]
    [InlineData("checkpoints/cp-1.dsse.json", AefLimits.MaxEnvelopeBytes)]
    [InlineData("run.json", AefLimits.MaxJsonBytes)]
    [InlineData("summary.json", AefLimits.MaxJsonBytes)]
    [InlineData("overlays/seal-1.json", AefLimits.MaxJsonBytes)]
    [InlineData("ext/seal.json.bak", AefLimits.MaxJsonBytes)]
    [InlineData("results.ndjson", AefLimits.MaxJsonBytes)]
    public void TheSizeLimitOfAFile_GoesByItsRole(string path, int limit)
    {
        Assert.Equal(limit, AefLimits.MaxBytesOf(path));
    }

    [Fact]
    public void NumbersKeepTheirBinary64Value()
    {
        var doc = Parse("""{"big": 9007199254740993, "small": 0.1}""");

        Assert.Equal(9007199254740992.0, (double)doc["big"]!);
        Assert.Equal(0.1, (double)doc["small"]!);
    }
}
