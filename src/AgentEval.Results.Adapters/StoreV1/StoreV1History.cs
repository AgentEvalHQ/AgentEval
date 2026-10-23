// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Adapters.StoreV1;

internal sealed record StoreV1HistoryRecord(int SourceOrdinal, HistoryEntry Entry, JsonElement Recorded);

// Legacy index parsing only: recorded index values are claims, not reconstructed execution outcomes.
internal static class StoreV1History
{
    internal const string EvidenceId = "E-history";
    internal const int MaxRecords = 100_000;
    internal const long MaxBytes = 64L << 20;

    internal static IReadOnlyList<StoreV1HistoryRecord>? ReadFile(string path, CancellationToken ct, AefReadBudget? budget)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!Path.Exists(path)) return null;
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse) throw Bad("Ordinary-file classification is unavailable.");
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (AefFolder.KindOf(parent) != AefEntryKind.Folder) throw Bad("History source ancestry is not ordinary directories.");
        if (AefFolder.KindOf(path) != AefEntryKind.File) throw Bad("History source is not an ordinary file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = input.Length;
        if (size > (budget?.Limit(MaxBytes) ?? MaxBytes)) throw Bad("History exceeds the bounded input byte limit.");
        budget?.Charge(size);
        var bytes = new byte[checked((int)size)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw Bad("History changed while being captured.");
        try
        {
            var start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            using var lines = new StringReader(new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start));
            var records = new List<StoreV1HistoryRecord>();
            var lineCount = 0;
            while (lines.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (++lineCount > MaxRecords) throw Bad("History exceeds the bounded line count.");
                if (string.IsNullOrWhiteSpace(line)) continue;
                var encoded = Encoding.UTF8.GetBytes(line);
                AefJsonReader.Check(encoded, checked((int)MaxBytes));
                using var json = JsonDocument.Parse(encoded);
                records.Add(Parse(json.RootElement, records.Count + 1));
            }
            return records;
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException or AefEncodingException)
        { throw Bad("History is not readable, unambiguous UTF-8 JSONL.", error); }
    }

    internal static StoreV1HistoryRecord Parse(JsonElement raw, int ordinal)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("runId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || !raw.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String
            || !timestamp.TryGetDateTimeOffset(out var time) || !HasOffset(timestamp.GetString()!)
            || !raw.TryGetProperty("verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(verdict.GetString())
            || !raw.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number
            || !score.TryGetDouble(out var value) || !double.IsFinite(value)) throw Bad("A history entry omits a required recorded fact.");
        var notes = raw.TryGetProperty("notes", out var note) ? note : default;
        if (notes.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
            throw Bad("History notes must be text or null.");
        return new(ordinal, new(id.GetString()!, time, verdict.GetString()!, value,
            notes.ValueKind == JsonValueKind.String ? notes.GetString() : null), raw.Clone());
    }

    internal static byte[] Encode(IReadOnlyList<StoreV1HistoryRecord> records) => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
    {
        ["version"] = 1,
        ["entries"] = new JsonArray(records.Select(r => (JsonNode)new JsonObject
        {
            ["sourceOrdinal"] = r.SourceOrdinal,
            ["entry"] = JsonNode.Parse(r.Recorded.GetRawText())
        }).ToArray())
    });

    private static bool HasOffset(string value) => value.EndsWith('Z') ||
        value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':';
    private static InvalidDataException Bad(string actual, Exception? inner = null) => new(
        "Expected: complete bounded recorded history entries. Actual: " + actual +
        " Suggestions: inspect the original history index. Because: missing facts cannot become constructor defaults or silently disappear.", inner);
}
