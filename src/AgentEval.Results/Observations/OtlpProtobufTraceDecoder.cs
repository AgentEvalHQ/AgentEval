// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace AgentEval.Results.Observations;

/// <summary>Bounded OTLP trace protobuf decoder, mapped to opentelemetry-proto v1.9.0.</summary>
/// <remarks>Unknown fields are skipped; singular messages merge, repeated fields append, and oneof alternatives replace.</remarks>
public sealed class OtlpProtobufTraceDecoder : IOtlpTraceDecoder
{
    private readonly OtlpIngestionLimits _limits;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Creates a decoder with validated immutable resource budgets.</summary>
    public OtlpProtobufTraceDecoder(OtlpIngestionLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public OtlpTraceBatch Decode(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > _limits.MaxDecodedBytes) throw Limit("decoded byte budget exceeded");
        try
        {
            var parser = new Parser(_limits, cancellationToken);
            var root = parser.Parse(payload.Span, Message.Batch, 1);
            var result = new OtlpTraceBatch(Many(root, 1, ResourceSpans));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (DecoderFallbackException) { throw Malformed(); }
    }

    private static OtlpIngestionException Malformed() => new("malformed", "invalid OTLP protobuf message");
    private static OtlpIngestionException Limit(string reason) => new("limit", reason);

    private enum Message { Batch, ResourceSpans, Resource, EntityRef, ScopeSpans, Scope, Span, Event, Link, Status, KeyValue, AnyValue, ArrayValue, KeyValueList }
    private enum Type { Varint, Fixed64, Fixed32, String, Bytes, Message }
    private readonly record struct Field(Type Type, bool Repeated = false, Message Message = default)
    {
        internal int Wire => Type switch { Type.Varint => 0, Type.Fixed64 => 1, Type.Fixed32 => 5, _ => 2 };
    }
    private static Field? Definition(Message owner, int field) => (owner, field) switch
    {
        (Message.Batch, 1) => new(Type.Message, true, Message.ResourceSpans),
        (Message.ResourceSpans, 1) => new(Type.Message, false, Message.Resource),
        (Message.ResourceSpans, 2) => new(Type.Message, true, Message.ScopeSpans),
        (Message.ResourceSpans, 3) => new(Type.String),
        (Message.Resource, 1) => new(Type.Message, true, Message.KeyValue),
        (Message.Resource, 2) => new(Type.Varint),
        (Message.Resource, 3) => new(Type.Message, true, Message.EntityRef),
        (Message.EntityRef, 1 or 2) => new(Type.String),
        (Message.EntityRef, 3 or 4) => new(Type.String, true),
        (Message.ScopeSpans, 1) => new(Type.Message, false, Message.Scope),
        (Message.ScopeSpans, 2) => new(Type.Message, true, Message.Span),
        (Message.ScopeSpans, 3) => new(Type.String),
        (Message.Scope, 1 or 2) => new(Type.String),
        (Message.Scope, 3) => new(Type.Message, true, Message.KeyValue),
        (Message.Scope, 4) => new(Type.Varint),
        (Message.Span, 1 or 2 or 4) => new(Type.Bytes),
        (Message.Span, 3 or 5) => new(Type.String),
        (Message.Span, 6 or 10 or 12 or 14) => new(Type.Varint),
        (Message.Span, 7 or 8) => new(Type.Fixed64),
        (Message.Span, 9) => new(Type.Message, true, Message.KeyValue),
        (Message.Span, 11) => new(Type.Message, true, Message.Event),
        (Message.Span, 13) => new(Type.Message, true, Message.Link),
        (Message.Span, 15) => new(Type.Message, false, Message.Status),
        (Message.Span, 16) => new(Type.Fixed32),
        (Message.Event, 1) => new(Type.Fixed64),
        (Message.Event, 2) => new(Type.String),
        (Message.Event, 3) => new(Type.Message, true, Message.KeyValue),
        (Message.Event, 4) => new(Type.Varint),
        (Message.Link, 1 or 2) => new(Type.Bytes),
        (Message.Link, 3) => new(Type.String),
        (Message.Link, 4) => new(Type.Message, true, Message.KeyValue),
        (Message.Link, 5) => new(Type.Varint),
        (Message.Link, 6) => new(Type.Fixed32),
        (Message.Status, 2) => new(Type.String),
        (Message.Status, 3) => new(Type.Varint),
        (Message.KeyValue, 1) => new(Type.String),
        (Message.KeyValue, 2) => new(Type.Message, false, Message.AnyValue),
        (Message.AnyValue, 1) => new(Type.String),
        (Message.AnyValue, 2 or 3) => new(Type.Varint),
        (Message.AnyValue, 4) => new(Type.Fixed64),
        (Message.AnyValue, 5) => new(Type.Message, false, Message.ArrayValue),
        (Message.AnyValue, 6) => new(Type.Message, false, Message.KeyValueList),
        (Message.AnyValue, 7) => new(Type.Bytes),
        (Message.ArrayValue, 1) => new(Type.Message, true, Message.AnyValue),
        (Message.KeyValueList, 1) => new(Type.Message, true, Message.KeyValue),
        _ => null
    };

    private sealed class Node
    {
        internal readonly Dictionary<int, object> Fields = [];
    }

    private sealed class Parser(OtlpIngestionLimits limits, CancellationToken token)
    {
        private int _items;
        private int _spans;

        internal Node Parse(ReadOnlySpan<byte> bytes, Message kind, int depth, Node? existing = null)
        {
            token.ThrowIfCancellationRequested();
            if (depth > limits.MaxDepth) throw Limit("message depth budget exceeded");
            if (existing is null && kind == Message.Span && ++_spans > limits.MaxSpans) throw Limit("span budget exceeded");
            var node = existing ?? new Node();
            var reader = new WireReader(bytes);
            while (!reader.End)
            {
                Count();
                var (id, wire) = reader.Tag();
                var definition = Definition(kind, id);
                if (definition is not { } field || field.Wire != wire)
                {
                    Skip(ref reader, id, wire, depth);
                    continue;
                }
                if (kind == Message.AnyValue && !node.Fields.ContainsKey(id)) node.Fields.Clear();
                object value;
                if (field.Type == Type.Message)
                {
                    var previous = !field.Repeated && node.Fields.TryGetValue(id, out var old) ? (Node)old : null;
                    value = Parse(reader.Block(), field.Message, depth + 1, previous);
                }
                else if (field.Type is Type.String or Type.Bytes)
                {
                    var block = reader.Block();
                    if (block.Length > limits.MaxValueBytes) throw Limit("string or bytes value budget exceeded");
                    value = field.Type == Type.String ? Utf8.GetString(block) : block.ToArray();
                }
                else value = field.Type switch
                {
                    Type.Varint => reader.Varint(),
                    Type.Fixed64 => reader.Fixed64(),
                    Type.Fixed32 => (ulong)reader.Fixed32(),
                    _ => throw Malformed()
                };
                if (field.Repeated)
                {
                    if (!node.Fields.TryGetValue(id, out var list)) node.Fields[id] = list = new List<object>();
                    var values = (List<object>)list;
                    if (field.Type == Type.Message && field.Message == Message.KeyValue && values.Count >= limits.MaxAttributes)
                        throw Limit("attribute budget exceeded");
                    values.Add(value);
                }
                else node.Fields[id] = value;
            }
            return node;
        }

        private void Count()
        {
            token.ThrowIfCancellationRequested();
            if (++_items > limits.MaxItems) throw Limit("aggregate field budget exceeded");
        }

        private void Skip(ref WireReader reader, int id, int wire, int depth)
        {
            switch (wire)
            {
                case 0: reader.Varint(); break;
                case 1: reader.Fixed64(); break;
                case 2: reader.Block(); break;
                case 3:
                    if (depth >= limits.MaxDepth) throw Limit("unknown group depth budget exceeded");
                    while (!reader.End)
                    {
                        Count();
                        var (childId, childWire) = reader.Tag();
                        if (childWire == 4)
                        {
                            if (childId != id) throw Malformed();
                            return;
                        }
                        Skip(ref reader, childId, childWire, depth + 1);
                    }
                    throw Malformed();
                case 5: reader.Fixed32(); break;
                default: throw Malformed();
            }
        }
    }

    private ref struct WireReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        internal bool End => _bytes.IsEmpty;
        internal ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (_bytes.IsEmpty) throw Malformed();
                var next = _bytes[0];
                _bytes = _bytes[1..];
                if (shift == 63 && next > 1) throw Malformed();
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw Malformed();
        }
        internal (int Id, int Wire) Tag()
        {
            var tag = Varint();
            if (tag > uint.MaxValue || tag >> 3 == 0) throw Malformed();
            return ((int)(tag >> 3), (int)(tag & 7));
        }
        internal ReadOnlySpan<byte> Block()
        {
            var size = Varint();
            if (size > (ulong)_bytes.Length) throw Malformed();
            return Take((int)size);
        }
        internal ulong Fixed64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        internal uint Fixed32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        private ReadOnlySpan<byte> Take(int size)
        {
            if (size > _bytes.Length) throw Malformed();
            var result = _bytes[..size];
            _bytes = _bytes[size..];
            return result;
        }
    }

    private static string Text(Node node, int field) => node.Fields.TryGetValue(field, out var value) ? (string)value : "";
    private static ulong Number(Node node, int field) => node.Fields.TryGetValue(field, out var value) ? (ulong)value : 0;
    private static uint UInt32(Node node, int field) => unchecked((uint)Number(node, field));
    private static Node? Child(Node node, int field) => node.Fields.TryGetValue(field, out var value) ? (Node)value : null;
    private static ImmutableArray<T> Many<T>(Node node, int field, Func<Node, T> project) =>
        node.Fields.TryGetValue(field, out var values) ? ((List<object>)values).Select(value => project((Node)value)).ToImmutableArray() : [];
    private static ImmutableArray<string> Strings(Node node, int field) =>
        node.Fields.TryGetValue(field, out var values) ? ((List<object>)values).Cast<string>().ToImmutableArray() : [];
    private static string Id(Node node, int field, int length, bool optional = false)
    {
        var bytes = node.Fields.TryGetValue(field, out var value) ? (byte[])value : [];
        if (optional && bytes.Length == 0) return "";
        if (bytes.Length != length || bytes.All(b => b == 0)) throw Malformed();
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    private static OtlpResourceSpans ResourceSpans(Node node) => new(
        Child(node, 1) is { } resource ? new(Attributes(resource, 1), UInt32(resource, 2), Many(resource, 3, EntityRef)) : null,
        Text(node, 3), Many(node, 2, ScopeSpans));
    private static OtlpEntityRef EntityRef(Node node) => new(Text(node, 1), Text(node, 2), Strings(node, 3), Strings(node, 4));
    private static OtlpScopeSpans ScopeSpans(Node node) => new(
        Child(node, 1) is { } scope ? new(Text(scope, 1), Text(scope, 2), Attributes(scope, 3), UInt32(scope, 4)) : null,
        Text(node, 3), Many(node, 2, Span));
    private static OtlpSpan Span(Node node) => new(Id(node, 1, 16), Id(node, 2, 8), Id(node, 4, 8, optional: true),
        Text(node, 3), Text(node, 5), unchecked((int)Number(node, 6)), Number(node, 7), Number(node, 8), UInt32(node, 16),
        Attributes(node, 9), UInt32(node, 10), Many(node, 11, Event), UInt32(node, 12), Many(node, 13, Link), UInt32(node, 14),
        Child(node, 15) is { } status ? new(unchecked((int)Number(status, 3)), Text(status, 2)) : null);
    private static OtlpSpanEvent Event(Node node) => new(Number(node, 1), Text(node, 2), Attributes(node, 3), UInt32(node, 4));
    private static OtlpSpanLink Link(Node node) => new(Id(node, 1, 16), Id(node, 2, 8), Text(node, 3), UInt32(node, 6), Attributes(node, 4), UInt32(node, 5));
    private static ImmutableArray<OtlpAttribute> Attributes(Node node, int field)
    {
        var attributes = Many(node, field, item => new OtlpAttribute(Text(item, 1), Value(Child(item, 2))));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
            if (!keys.Add(attribute.Key)) throw Malformed();
        return attributes;
    }
    private static OtlpValue Value(Node? node)
    {
        if (node is null || node.Fields.Count == 0) return new OtlpValue.Empty();
        var field = node.Fields.Single();
        return field.Key switch
        {
            1 => new OtlpValue.Text((string)field.Value),
            2 => new OtlpValue.Boolean((ulong)field.Value != 0),
            3 => new OtlpValue.Integer(unchecked((long)(ulong)field.Value)),
            4 => new OtlpValue.Real(BitConverter.Int64BitsToDouble(unchecked((long)(ulong)field.Value))),
            5 => new OtlpValue.Array(Many((Node)field.Value, 1, n => Value(n))),
            6 => new OtlpValue.KeyValues(Attributes((Node)field.Value, 1)),
            7 => new OtlpValue.Bytes(ImmutableArray.CreateRange((byte[])field.Value)),
            _ => throw Malformed()
        };
    }
}
