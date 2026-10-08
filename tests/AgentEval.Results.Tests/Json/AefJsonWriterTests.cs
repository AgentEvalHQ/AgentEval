using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Tests.Json;

/// <summary>What <see cref="AefJsonWriter"/> writes: LF only, no BOM, integers in plain digits, nothing a reader refuses.</summary>
public class AefJsonWriterTests
{
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void ADocument_IsIndentedWithLfOnly_AndEndsInLf()
    {
        var bytes = AefJsonWriter.Document(new JsonObject { ["a"] = 1, ["b"] = new JsonArray(1, 2), ["c"] = new JsonObject(), ["d"] = new JsonArray() });

        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ],\n  \"c\": {},\n  \"d\": []\n}\n", Text(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    public void ALine_IsCompactAndEndsInLf()
    {
        Assert.Equal("{\"a\":[1,{\"b\":null}],\"c\":true}\n", Text(AefJsonWriter.Line(new JsonObject
        {
            ["a"] = new JsonArray(1, new JsonObject { ["b"] = null }),
            ["c"] = true,
        })));
    }

    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(-0.0, "0")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(9007199254740991.0, "9007199254740991")]
    [InlineData(-9007199254740991.0, "-9007199254740991")]
    [InlineData(0.1, "0.1")]
    [InlineData(2.5, "2.5")]
    [InlineData(1e-7, "1E-07")]
    [InlineData(1e300, "1E+300")]
    public void Numbers_AreTheBinary64Value_IntegersInPlainDigits(double value, string written)
    {
        Assert.Equal(written, AefJsonWriter.FormatNumber(value));
        Assert.Equal(value, double.Parse(written, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnIntegerReadFromAnotherSpelling_IsWrittenInPlainDigits()
    {
        var read = AefJsonReader.ParseDocument("""{"a": 2.0, "b": 2e0, "c": 1E3, "d": 0.5}"""u8);

        Assert.Equal("{\"a\":2,\"b\":2,\"c\":1000,\"d\":0.5}\n", Text(AefJsonWriter.Line(read)));
    }

    [Fact]
    public void NumbersHeldAsOtherClrTypes_AreWrittenTheSameWay()
    {
        Assert.Equal("{\"i\":3,\"l\":4,\"m\":0.25,\"f\":1.5}\n", Text(AefJsonWriter.Line(new JsonObject
        {
            ["i"] = 3,
            ["l"] = 4L,
            ["m"] = 0.25m,
            ["f"] = 1.5f,
        })));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NaNAndInfinity_AreRefused(double value)
    {
        Assert.Throws<ArgumentException>(() => AefJsonWriter.FormatNumber(value));
    }

    // Built at run time: xunit carries a lone surrogate in InlineData as U+FFFD.
    public static TheoryData<int[]> UnpairedSurrogates() => new() { new[] { 0xD800 }, new[] { 'a', 0xDC00 }, new[] { 0xDC00, 0xD800 } };

    [Theory]
    [MemberData(nameof(UnpairedSurrogates))]
    public void AnUnpairedSurrogate_IsRefused(int[] units)
    {
        var text = new string(units.Select(u => (char)u).ToArray());

        Assert.Throws<ArgumentException>(() => AefJsonWriter.Line(new JsonObject { ["s"] = text }));
        Assert.Throws<ArgumentException>(() => AefJsonWriter.Line(new JsonObject { [text] = 1 }));
    }

    [Fact]
    public void Strings_EscapeOnlyQuotesBackslashesAndC0_AndKeepOtherTextAsUtf8()
    {
        var separator = (char)0x2028;   // a line separator: text, not a line break ([ENC-6])
        var delete = (char)0x7F;
        var text = $"q\"b\\n\nt\tc{(char)1} é \U0001D11E {separator} / {delete}";

        var line = Text(AefJsonWriter.Line(new JsonObject { ["s"] = text }));

        Assert.Equal("{\"s\":\"q\\\"b\\\\n\\nt\\tc\\u0001 é \U0001D11E " + separator + " / " + delete + "\"}\n", line);
    }

    [Fact]
    public void WhatIsWritten_ReadsBackAsTheSameValue()
    {
        var original = AefJsonReader.ParseDocument(
            """{"s": "a\u0000b𝄞", "n": [0, -1, 0.1, 1e-300, 123456789012], "o": {"x": null, "y": false}}"""u8);

        var again = AefJsonReader.ParseDocument(AefJsonWriter.Document(original));

        // The same values, so written the same way (JsonNode.DeepEquals compares number text on .NET 8: 1e-300 is not 1E-300).
        Assert.Equal(AefJsonWriter.Line(original), AefJsonWriter.Line(again));
        Assert.Equal(AefJsonWriter.Line(again), AefJsonWriter.Line(AefJsonReader.ParseDocument(AefJsonWriter.Line(again))));
        Assert.Equal(1e-300, (double)again["n"]![3]!);
        Assert.Equal("a\0b\U0001D11E", (string)again["s"]!);
    }

    [Fact]
    public void Depth64_IsWritten_AndDeeperIsRefused()
    {
        static JsonObject Nested(int depth)
        {
            JsonNode inner = new JsonArray();
            for (var i = 2; i < depth; i++) inner = new JsonArray(inner);
            return new JsonObject { ["x"] = inner };   // the object is depth 1, the arrays 2 to depth
        }

        _ = AefJsonReader.ParseDocument(AefJsonWriter.Line(Nested(64)));
        Assert.Throws<ArgumentException>(() => AefJsonWriter.Line(Nested(65)));
        Assert.Throws<ArgumentException>(() => AefJsonWriter.Document(Nested(65)));
    }

    [Fact]
    public void ADocumentOrLineAboveFourMebibytes_IsRefused()
    {
        var big = new JsonObject { ["s"] = new string('a', AefLimits.MaxJsonBytes) };

        Assert.Throws<ArgumentException>(() => AefJsonWriter.Line(big));
        Assert.Throws<ArgumentException>(() => AefJsonWriter.Document(big));
        Assert.NotEmpty(AefJsonWriter.Document(big, AefLimits.MaxSealBytes));   // a seal or an envelope: 32 MiB
        Assert.NotEmpty(AefJsonWriter.Compact(big));   // a tool's output has no such limit
    }
}
