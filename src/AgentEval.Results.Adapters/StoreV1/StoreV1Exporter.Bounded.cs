// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    internal static async Task<AefConversion> ExportSemanticBoundedAsync(IOutputStoreReader store, string runId,
        string outputDirectory, CancellationToken ct, AefReadBudget budget)
    {
        var input = new AefWriteBudget(budget.Limit(64L << 20), ct, budget.Charge);
        var run = await ReadAsync(store, runId, ct, input).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Export(run, outputDirectory, new()
        {
            WriteBudget = new(budget.Limit(64L << 20), ct, budget.Charge),
        });
    }

    // Reuse serializer metadata for the inventory/run. Output-store DTOs contain
    // strings, scalar values and collections; this converter covers nested values
    // and dictionary keys before the serializer allocates escape/transcode buffers.
    // The store still owns allocations made before it returns a record.
    internal sealed class SemanticAccountant
    {
        private readonly AefWriteBudget _budget;
        private readonly JsonSerializerOptions _options;

        internal SemanticAccountant(AefWriteBudget budget)
        {
            _budget = budget;
            _options = new(s_store);
            _options.Converters.Insert(0, new BoundedStringConverter(budget));
        }

        internal void Account<T>(T value)
        {
            var buffer = new AccountingBuffer(_budget);
            using var writer = new Utf8JsonWriter(buffer, new() { Encoder = _options.Encoder });
            JsonSerializer.Serialize(writer, value, _options);
        }
    }

    private sealed class BoundedStringConverter(AefWriteBudget budget) : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            Check(writer, value);
            writer.WriteStringValue(value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            Check(writer, value);
            writer.WritePropertyName(value);
        }

        private void Check(Utf8JsonWriter writer, string value)
        {
            writer.Flush(); // Charge preceding bytes before reserving scratch space.
            // A UTF-16 code unit may become six JSON bytes (\uXXXX). Reserve that
            // conservative token capacity, but charge only the bytes actually emitted.
            budget.Check(6L * value.Length + 2);
        }
    }

    // Check capacity before allocating a serializer buffer, including collections
    // containing only null/scalar values that never invoke the string converter.
    private sealed class AccountingBuffer(AefWriteBudget budget) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        public void Advance(int count) => budget.Charge(count);
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var needed = Math.Max(1, sizeHint);
            budget.Check(needed);
            if (_buffer.Length < needed) _buffer = new byte[needed];
            return _buffer.AsMemory(0, (int)Math.Min(_buffer.Length, budget.Remaining));
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
