// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Rest;

internal static partial class BinaryEndpoints
{
    private static readonly JsonSerializerOptions s_historyJson = new(s_traceJson) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static void MapHistoryDownload(this WebApplication app) => app.MapGet("/api/v1/subjects/{kind}/{name}/history", async (
        HttpContext context, string kind, string name, IOutputStoreReader store, IWorkspaceRunSource source, CancellationToken ct) =>
    {
        kind = kind.ToLowerInvariant();
        if (kind is not ("agent" or "workflow" or "model" or "endpoint" or "mcp-server" or "other"))
            return HttpResults.BadRequest("Unknown subject kind.");
        if (!FileSystemLayout.IsSafePathSegment(name)) return HttpResults.BadRequest("Invalid subject name.");
        try
        {
            var rows = await new WorkspaceHistorySource(store, source).ReadAsync(kind, name, ct);
            if (rows is null) return HttpResults.NotFound();
            // Verify and bound the whole download before emitting any JSONL prefix.
            var text = new StringBuilder();
            long bytes = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var line = JsonSerializer.Serialize(row, s_historyJson);
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                if (bytes > 64L << 20) throw new InvalidDataException("The history download exceeds 64 MiB.");
                text.Append(line).Append('\n');
            }
            context.Response.Headers.CacheControl = "no-store";
            return HttpResults.Content(text.ToString(), "application/x-ndjson", Encoding.UTF8);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        { return InvalidEvidence(); }
    });
}
