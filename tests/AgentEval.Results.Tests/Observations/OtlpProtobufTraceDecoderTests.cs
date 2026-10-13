// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Observations;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpProtobufTraceDecoderTests
{
    private static byte[] Fixture(string name, string extension) => File.ReadAllBytes(Path.Combine(
        AefCorpus.RepoRoot, "tests", "AgentEval.Results.Tests", "Observations", "Fixtures", name + extension));
    private static OtlpTraceBatch Decode(byte[] bytes, OtlpIngestionLimits? limits = null) => new OtlpProtobufTraceDecoder(limits).Decode(bytes);

    [Theory]
    [InlineData("complete")]
    [InlineData("merge-and-unknown")]
    [InlineData("wire-scalars")]
    public void Decode_IndependentGoogleProtobufFixture_MatchesIndependentJson(string name)
    {
        var expected = new OtlpJsonTraceDecoder().Decode(Fixture(name, ".json"));
        var actual = Decode(Fixture(name, ".pb"));
        Assert.Equivalent(expected, actual, strict: true);
    }

    [Fact]
    public void Decode_MergedMessagesAndOneof_PreservesMergeAndReplacementSemantics()
    {
        var scope = Decode(Fixture("merge-and-unknown", ".pb")).Resources[0].Scopes[0];
        Assert.Equal("first", scope.Scope!.Name);
        Assert.Equal("merged-version", scope.Scope.Version);
        var span = Assert.Single(scope.Spans);
        Assert.Equal("final-name", span.Name);
        Assert.Equal(new OtlpSpanStatus(2, "merged-status"), span.Status);
        var replaced = Assert.IsType<OtlpValue.Array>(span.Attributes[0].Value);
        Assert.Equal("final", Assert.IsType<OtlpValue.Text>(Assert.Single(replaced.Values)).Value);
        var merged = Assert.IsType<OtlpValue.Array>(span.Attributes[1].Value);
        Assert.Equal(new[] { "old", "append" }, merged.Values.Select(v => Assert.IsType<OtlpValue.Text>(v).Value));
    }

    [Theory]
    [InlineData("00")] // zero field number
    [InlineData("80")] // incomplete tag
    [InlineData("0affffffff0f")] // oversized block
    [InlineData("0a0200")] // truncated block
    [InlineData("0b")] // unterminated unknown group (known field with wrong wire)
    [InlineData("0b14")] // mismatched group end
    [InlineData("0c")] // top-level group end
    [InlineData("0e")] // invalid wire type
    [InlineData("09ff")] // incomplete fixed64
    [InlineData("0dff")] // incomplete fixed32
    [InlineData("08ffffffffffffffffffff02")] // overflowing varint
    [InlineData("ffffffff1f01")] // tag exceeds uint32
    public void Decode_MalformedWire_RefusesWithoutRuntimeExceptions(string hex) =>
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Decode(Convert.FromHexString(hex))).Code);

    [Theory]
    [InlineData("")]
    [InlineData("0801")] // known field number, incompatible wire type: skipped as unknown
    [InlineData("0b08010c")] // well-formed unknown group
    public void Decode_EmptyOrUnknownFields_ReturnsEmptyBatch(string hex) => Assert.Empty(Decode(Convert.FromHexString(hex)).Resources);

    [Fact]
    public void Decode_WholeRequestBudgets_RefusesWithoutPartialResult()
    {
        var fixture = Fixture("complete", ".pb");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(fixture, new() { MaxDecodedBytes = fixture.Length - 1 })).Code);
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(fixture, new() { MaxItems = 2 })).Code);
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode([.. fixture, .. fixture], new() { MaxSpans = 1 })).Code);
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(fixture, new() { MaxValueBytes = 2 })).Code);
        var twoAttributes = Fixture("merge-and-unknown", ".pb");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(twoAttributes, new() { MaxAttributes = 1 })).Code);
        var groups = Enumerable.Repeat((byte)0x0b, 10).Concat(Enumerable.Repeat((byte)0x0c, 10)).ToArray();
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(groups, new() { MaxDepth = 8 })).Code);
    }

    [Fact]
    public void Decode_InvalidUtf8KnownString_Refuses()
    {
        // Batch.resourceSpans[0].schemaUrl = one invalid UTF-8 byte.
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Decode([0x0a, 0x03, 0x1a, 0x01, 0xff])).Code);
    }

    [Fact]
    public void Decode_Cancelled_RefusesBeforeReading() => Assert.ThrowsAny<OperationCanceledException>(() =>
        new OtlpProtobufTraceDecoder().Decode(Fixture("complete", ".pb"), new(true)));

    [Fact]
    public void Decode_CallerMutatesInput_RetainsOwnedSnapshot()
    {
        var wire = Fixture("complete", ".pb");
        var result = Decode(wire);
        Array.Fill(wire, (byte)0);
        var span = result.Resources[0].Scopes[0].Spans[0];
        Assert.Equal("op", span.Name);
        Assert.Equal(ulong.MaxValue, span.EndTimeUnixNano);
        var values = Assert.IsType<OtlpValue.Array>(span.Attributes[0].Value).Values;
        Assert.Equal(long.MinValue, Assert.IsType<OtlpValue.Integer>(values[2]).Value);
        Assert.Equal(new byte[] { 0, 251, 255 }, Assert.IsType<OtlpValue.Bytes>(values[6]).Value);
    }

    [Fact]
    public void Decode_MutatedWire_ProducesOnlyBoundedModelOrStructuredRefusal()
    {
        var source = Fixture("complete", ".pb");
        var decoder = new OtlpProtobufTraceDecoder(new() { MaxItems = 5000, MaxDepth = 16 });
        var random = new Random(612);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var bytes = source.ToArray();
            if (iteration % 2 == 0) bytes = bytes[..random.Next(bytes.Length)];
            else bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            try { Assert.NotNull(decoder.Decode(bytes)); }
            catch (OtlpIngestionException error) { Assert.Contains(error.Code, new[] { "malformed", "limit" }); }
        }
    }
}
