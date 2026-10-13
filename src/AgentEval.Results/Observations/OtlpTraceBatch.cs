// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Results.Observations;

/// <summary>Decoded telemetry, not an evaluation verdict or durable acceptance receipt.</summary>
public sealed record OtlpTraceBatch(ImmutableArray<OtlpResourceSpans> Resources);
/// <summary>Resource provenance and its independently versioned instrumentation scopes.</summary>
public sealed record OtlpResourceSpans(OtlpResource? Resource, string SchemaUrl, ImmutableArray<OtlpScopeSpans> Scopes);
/// <summary>Resource attributes and producer-reported dropped attributes.</summary>
public sealed record OtlpResource(ImmutableArray<OtlpAttribute> Attributes, uint DroppedAttributesCount,
    ImmutableArray<OtlpEntityRef> EntityRefs);
/// <summary>Resource entity references; these do not assert verified identity.</summary>
public sealed record OtlpEntityRef(string SchemaUrl, string Type, ImmutableArray<string> IdKeys, ImmutableArray<string> DescriptionKeys);
/// <summary>Scope metadata and spans sharing its schema URL.</summary>
public sealed record OtlpScopeSpans(OtlpScope? Scope, string SchemaUrl, ImmutableArray<OtlpSpan> Spans);
/// <summary>Instrumentation scope name, version and attributes.</summary>
public sealed record OtlpScope(string Name, string Version, ImmutableArray<OtlpAttribute> Attributes, uint DroppedAttributesCount);
/// <summary>A span with exact nanosecond timestamps, full flag bits, and numeric forward-compatible enum values.</summary>
public sealed record OtlpSpan(string TraceId, string SpanId, string ParentSpanId, string TraceState, string Name,
    int Kind, ulong StartTimeUnixNano, ulong EndTimeUnixNano, uint Flags,
    ImmutableArray<OtlpAttribute> Attributes, uint DroppedAttributesCount,
    ImmutableArray<OtlpSpanEvent> Events, uint DroppedEventsCount,
    ImmutableArray<OtlpSpanLink> Links, uint DroppedLinksCount, OtlpSpanStatus? Status);
/// <summary>A producer-reported span event.</summary>
public sealed record OtlpSpanEvent(ulong TimeUnixNano, string Name, ImmutableArray<OtlpAttribute> Attributes, uint DroppedAttributesCount);
/// <summary>A span link, retaining all flag bits and producer-reported losses.</summary>
public sealed record OtlpSpanLink(string TraceId, string SpanId, string TraceState, uint Flags,
    ImmutableArray<OtlpAttribute> Attributes, uint DroppedAttributesCount);
/// <summary>Telemetry status; never interpreted as an AgentEval result.</summary>
public sealed record OtlpSpanStatus(int Code, string Message);
/// <summary>A typed attribute. Collection order is retained; duplicate keys are refused by decoders.</summary>
public sealed record OtlpAttribute(string Key, OtlpValue Value);

/// <summary>An immutable OTLP AnyValue preserving its wire type and integer precision.</summary>
public abstract record OtlpValue
{
    /// <summary>An unspecified AnyValue.</summary>
    public sealed record Empty : OtlpValue;
    /// <summary>A UTF-8 string value.</summary>
    public sealed record Text(string Value) : OtlpValue;
    /// <summary>A boolean value.</summary>
    public sealed record Boolean(bool Value) : OtlpValue;
    /// <summary>A signed 64-bit integer.</summary>
    public sealed record Integer(long Value) : OtlpValue;
    /// <summary>A binary64 floating-point value, including the protobuf non-finite values.</summary>
    public sealed record Real(double Value) : OtlpValue;
    /// <summary>An immutable binary value.</summary>
    public sealed record Bytes(ImmutableArray<byte> Value) : OtlpValue;
    /// <summary>An ordered array of values.</summary>
    public sealed record Array(ImmutableArray<OtlpValue> Values) : OtlpValue;
    /// <summary>An ordered collection of uniquely keyed values.</summary>
    public sealed record KeyValues(ImmutableArray<OtlpAttribute> Values) : OtlpValue;
}

/// <summary>Decodes one serialization of an OTLP trace export request under explicit resource limits.</summary>
public interface IOtlpTraceDecoder
{
    /// <summary>Returns an immutable snapshot or a path-free refusal; never persists or acknowledges input.</summary>
    OtlpTraceBatch Decode(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
