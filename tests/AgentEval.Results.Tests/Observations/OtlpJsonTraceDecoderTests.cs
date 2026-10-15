// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Results.Observations;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpJsonTraceDecoderTests
{
    private const string TraceId = "0123456789ABCDEF0123456789ABCDEF";
    private const string SpanId = "0123456789ABCDEF";
    private static string Request(string fields = "") =>
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"" + TraceId
        + "\",\"spanId\":\"" + SpanId + "\"" + fields + "}]}]}]}";
    private static OtlpTraceBatch Decode(string json, OtlpIngestionLimits? limits = null) =>
        new OtlpJsonTraceDecoder(limits).Decode(Encoding.UTF8.GetBytes(json));
    private static OtlpSpan Span(string fields) => Decode(Request(fields)).Resources[0].Scopes[0].Spans[0];
    private static OtlpValue Value(string json) => Span(",\"attributes\":[{\"key\":\"value\",\"value\":" + json + "}]").Attributes[0].Value;

    [Fact]
    public void Decode_CompleteTrace_RetainsScopeEventsLinksLossesAndIntegerPrecision()
    {
        var json = """
        {"resourceSpans":[{"schemaUrl":"resource-schema","resource":{"attributes":[{"key":"service.name","value":{"stringValue":"fixture"}}],"droppedAttributesCount":3,
          "entityRefs":[{"schemaUrl":"entity-schema","type":"service","idKeys":["service.name"],"descriptionKeys":[]}]},
          "scopeSpans":[{"schemaUrl":"scope-schema","scope":{"name":"sdk","version":"1.0","attributes":[],"droppedAttributesCount":4},
            "spans":[{"traceId":"0123456789ABCDEF0123456789ABCDEF","spanId":"0123456789ABCDEF","parentSpanId":"FEDCBA9876543210",
              "traceState":"vendor=state","name":"operation","kind":99,"startTimeUnixNano":"18446744073709551614","endTimeUnixNano":18446744073709551615,"flags":4294967295,
              "attributes":[{"key":"large","value":{"intValue":"9223372036854775807"}}],"droppedAttributesCount":5,
              "events":[{"timeUnixNano":"9007199254740993","name":"event","attributes":[],"droppedAttributesCount":6}],"droppedEventsCount":7,
              "links":[{"traceId":"FEDCBA9876543210FEDCBA9876543210","spanId":"FEDCBA9876543210","traceState":"a=b","flags":4294967295,"attributes":[],"droppedAttributesCount":8}],
              "droppedLinksCount":9,"status":{"code":2,"message":"reported failure"}}]}]}]}
        """;
        var resource = Assert.Single(Decode(json).Resources);
        Assert.Equal("resource-schema", resource.SchemaUrl);
        Assert.Equal(3u, resource.Resource!.DroppedAttributesCount);
        Assert.Equal("service.name", Assert.Single(resource.Resource.EntityRefs).IdKeys[0]);
        var scope = Assert.Single(resource.Scopes);
        Assert.Equal("scope-schema", scope.SchemaUrl);
        Assert.Equal("sdk", scope.Scope!.Name);
        Assert.Equal("1.0", scope.Scope.Version);
        Assert.Equal(4u, scope.Scope.DroppedAttributesCount);
        var span = Assert.Single(scope.Spans);
        Assert.Equal(TraceId.ToLowerInvariant(), span.TraceId);
        Assert.Equal(SpanId.ToLowerInvariant(), span.SpanId);
        Assert.Equal("fedcba9876543210", span.ParentSpanId);
        Assert.Equal("vendor=state", span.TraceState);
        Assert.Equal("operation", span.Name);
        Assert.Equal(99, span.Kind);
        Assert.Equal(ulong.MaxValue - 1, span.StartTimeUnixNano);
        Assert.Equal(ulong.MaxValue, span.EndTimeUnixNano);
        Assert.Equal(uint.MaxValue, span.Flags);
        Assert.Equal(long.MaxValue, Assert.IsType<OtlpValue.Integer>(span.Attributes[0].Value).Value);
        Assert.Equal(5u, span.DroppedAttributesCount);
        Assert.Equal(9007199254740993UL, Assert.Single(span.Events).TimeUnixNano);
        Assert.Equal(6u, span.Events[0].DroppedAttributesCount);
        Assert.Equal(7u, span.DroppedEventsCount);
        Assert.Equal(uint.MaxValue, Assert.Single(span.Links).Flags);
        Assert.Equal(8u, span.Links[0].DroppedAttributesCount);
        Assert.Equal(9u, span.DroppedLinksCount);
        Assert.Equal(new OtlpSpanStatus(2, "reported failure"), span.Status);
    }

    [Theory]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("\"1.25e3\"", 1250)]
    [InlineData("100e-2", 1)]
    [InlineData("\"9007199254740993\"", 9007199254740993)]
    public void Decode_IntegerRepresentations_AreExact(string number, long expected) =>
        Assert.Equal(expected, Assert.IsType<OtlpValue.Integer>(Value("{\"intValue\":" + number + "}")).Value);

    [Theory]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("-9223372036854775809")]
    [InlineData("\"1.00000000000000000000000000000000000001\"")]
    [InlineData("\"1e-1000\"")]
    [InlineData("\"1e1000\"")]
    public void Decode_InvalidInteger_RefusesWithoutRounding(string number) =>
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Value("{\"intValue\":" + number + "}")).Code);

    [Fact]
    public void Decode_AllAnyValueKinds_PreservesTypedNestedValues()
    {
        var array = Assert.IsType<OtlpValue.Array>(Value("""
        {"arrayValue":{"values":[{}, {"boolValue":true}, {"doubleValue":"NaN"}, {"doubleValue":"1.25"},
          {"bytesValue":"-_8"}, {"kvlistValue":{"values":[{"key":"nested","value":{"stringValue":"text"}}]}}]}}
        """)).Values;
        Assert.IsType<OtlpValue.Empty>(array[0]);
        Assert.True(Assert.IsType<OtlpValue.Boolean>(array[1]).Value);
        Assert.True(double.IsNaN(Assert.IsType<OtlpValue.Real>(array[2]).Value));
        Assert.Equal(1.25, Assert.IsType<OtlpValue.Real>(array[3]).Value);
        Assert.Equal(new byte[] { 251, 255 }, Assert.IsType<OtlpValue.Bytes>(array[4]).Value);
        Assert.Equal("text", Assert.IsType<OtlpValue.Text>(Assert.IsType<OtlpValue.KeyValues>(array[5]).Values[0].Value).Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resourceSpans\":null,\"futureField\":{\"future\":[1,2,3]}}")]
    public void Decode_EmptyAndUnknownFields_ProducesEmptyBatch(string json) => Assert.Empty(Decode(json).Resources);

    [Theory]
    [InlineData(",\"traceId\":\"00000000000000000000000000000000\"")]
    [InlineData(",\"spanId\":\"short\"")]
    [InlineData(",\"kind\":\"SPAN_KIND_CLIENT\"")]
    [InlineData(",\"flags\":4294967296")]
    [InlineData(",\"startTimeUnixNano\":-1")]
    [InlineData(",\"attributes\":[{\"key\":\"x\"},{\"key\":\"x\"}]")]
    [InlineData(",\"events\":[null]")]
    [InlineData(",\"status\":[]")]
    public void Decode_InvalidKnownField_Refuses(string fields) =>
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Span(fields)).Code);

    [Fact]
    public void Decode_MultipleAnyValueAlternatives_RefusesAmbiguity() =>
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Value("{\"intValue\":1,\"stringValue\":\"x\"}")).Code);

    [Fact]
    public void Decode_ResourceBudgets_RefusesBeforeReturningPartialBatch()
    {
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(Request(), new() { MaxDecodedBytes = 10 })).Code);
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(Request(), new() { MaxItems = 2 })).Code);
        var twoSpans = Request().Replace("\"spans\":[", "\"spans\":[{\"traceId\":\"" + TraceId + "\",\"spanId\":\"" + SpanId + "\"},");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(twoSpans, new() { MaxSpans = 1 })).Code);
        var attributes = Request(",\"attributes\":[{\"key\":\"a\"},{\"key\":\"b\"}]");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(attributes, new() { MaxAttributes = 1 })).Code);
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(Request(",\"name\":\"" + new string('x', 33) + "\""), new() { MaxValueBytes = 32 })).Code);
    }

    [Fact]
    public void Decode_Cancelled_DoesNotReturnData() => Assert.ThrowsAny<OperationCanceledException>(() =>
        new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes(Request()), new(true)));

    [Fact]
    public void Decode_CallerChangesPayload_ResultOwnsItsData()
    {
        var payload = Encoding.UTF8.GetBytes(Request(",\"name\":\"retained\""));
        var batch = new OtlpJsonTraceDecoder().Decode(payload);
        Array.Fill(payload, (byte)'x');
        Assert.Equal("retained", batch.Resources[0].Scopes[0].Spans[0].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"resourceSpans\":[null]}")]
    [InlineData("{\"resourceSpans\":{}}")]
    [InlineData("{\"resourceSpans\":[{}],}")]
    public void Decode_InvalidEnvelope_Refuses(string json) =>
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Decode(json)).Code);

    [Fact]
    public void Decode_UnknownFieldInvalidUtf8_RefusesInvalidEncoding()
    {
        byte[] payload = [.. "{\"future\":\""u8.ToArray(), 0xff, .. "\"}"u8.ToArray()];
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => new OtlpJsonTraceDecoder().Decode(payload)).Code);
    }

    [Fact]
    public void Decode_DeepUnknownField_StillAppliesStructuralBounds()
    {
        var json = "{\"future\":" + new string('[', 10) + "0" + new string(']', 10) + "}";
        Assert.Throws<OtlpIngestionException>(() => Decode(json, new() { MaxDepth = 8 }));
        Assert.Empty(Decode(json, new() { MaxDepth = 16 }).Resources);
    }

    [Fact]
    public void Decode_MultibyteStringsAndBinaryValues_AppliesByteBudgets()
    {
        var text = Request(",\"name\":\"" + new string('\u00e9', 17) + "\"");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(text, new() { MaxValueBytes = 32 })).Code);
        var binary = Request(",\"attributes\":[{\"key\":\"bytes\",\"value\":{\"bytesValue\":\"" + Convert.ToBase64String(new byte[33]) + "\"}}]");
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => Decode(binary, new() { MaxValueBytes = 32 })).Code);
    }
}
