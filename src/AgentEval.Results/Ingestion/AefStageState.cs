// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

internal sealed record AefStageState(string Status, int Files, long Bytes, string? RunHash = null, string? SnapshotDigest = null)
{
    internal static void Write(AefIngestionStage stage, string status, int files, long bytes, string? runHash = null, string? snapshotDigest = null)
    {
        var temporary = Path.Combine(stage.Directory, "state.tmp");
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, new { schemaVersion = "1.0", id = stage.Id, status, files, bytes, runHash, snapshotDigest });
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(stage.Directory, "state.json"), overwrite: true);
    }

    internal static AefStageState Read(AefIngestionStage stage)
    {
        var path = Path.Combine(stage.Directory, "state.json");
        if (AefFolder.KindOf(path) != AefEntryKind.File)
            throw new AefFinalizationException("state", "non-regular stage metadata");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 4096) throw new AefFinalizationException("state", "invalid stage metadata size");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new AefFinalizationException("state", "stage metadata changed while reading");
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()
                || root.GetProperty("schemaVersion").GetString() != "1.0"
                || root.GetProperty("id").GetString() != stage.Id)
                throw new AefFinalizationException("state", "invalid stage metadata identity or shape");
            return new(root.GetProperty("status").GetString() ?? "", root.GetProperty("files").GetInt32(),
                root.GetProperty("bytes").GetInt64(), root.TryGetProperty("runHash", out var hash) ? hash.GetString() : null,
                root.TryGetProperty("snapshotDigest", out var snapshot) ? snapshot.GetString() : null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new AefFinalizationException("state", "invalid stage metadata");
        }
    }
}
