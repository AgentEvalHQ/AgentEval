// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.MissionControl.Services;

internal static class WorkspaceArtifactProjector
{
    internal static readonly string[] ReportFormats = ["markdown", "md", "html", "junit", "sarif", "xml"];

    internal static IReadOnlyList<WorkspaceArtifact> Artifacts(AefWorkspaceRun run) => run.Evidence.Evidence.Select(e =>
    {
        var id = e.GetProperty("evidenceId").GetString()!;
        return new WorkspaceArtifact(id, e.GetProperty("kind").GetString()!,
            e.TryGetProperty("description", out var description) ? description.GetString() : null,
            run.Evidence.CapturedContent(id) is not null, ReportFormat(e), IsLegacyTrace(run, e));
    }).ToArray();

    internal static string? ReportFormat(JsonElement evidence) =>
        evidence.GetProperty("kind").GetString() == "document" &&
        evidence.TryGetProperty("ext", out var ext) && ext.ValueKind == JsonValueKind.Object &&
        ext.TryGetProperty("agenteval.store-v1", out var legacy) && legacy.ValueKind == JsonValueKind.Object &&
        legacy.TryGetProperty("reportFormat", out var format) && format.ValueKind == JsonValueKind.String &&
        Array.IndexOf(ReportFormats, format.GetString()) >= 0 ? format.GetString() : null;

    internal static bool IsLegacyTrace(AefWorkspaceRun run, JsonElement evidence)
    {
        if (evidence.GetProperty("evidenceId").GetString() != "E-trace" ||
            evidence.GetProperty("kind").GetString() != "transcript") return false;
        if (evidence.TryGetProperty("ext", out var descriptorExt) && descriptorExt.ValueKind == JsonValueKind.Object &&
            descriptorExt.TryGetProperty("agenteval.store-v1", out var descriptor) && descriptor.ValueKind == JsonValueKind.Object &&
            descriptor.TryGetProperty("traceFormat", out var format))
            return format.ValueKind == JsonValueKind.String && format.GetString() == "agent-trace-v1";
        // Conversions made before traceFormat was added remain readable. A native
        // run's legacy alias alone says nothing about its captured payload shape.
        return run.Evidence.Info.Imported &&
            run.Evidence.Header.TryGetProperty("imported", out var imported) &&
            imported.GetProperty("from").GetString() is { } from &&
            (from == StoreV1Exporter.From || from.StartsWith(StoreV1Exporter.From + " (", StringComparison.Ordinal)) &&
            run.Evidence.Header.TryGetProperty("ext", out var ext) && ext.ValueKind == JsonValueKind.Object &&
            ext.TryGetProperty("agenteval.store-v1", out var legacy) && legacy.ValueKind == JsonValueKind.Object;
    }

    internal static ReadOnlyMemory<byte> ReadLegacyTrace(AefWorkspaceRun run, ReadOnlyMemory<byte> bytes)
    {
        bytes = JsonUtf8(bytes);
        using var document = JsonDocument.Parse(bytes);
        var trace = document.RootElement;
        if (trace.ValueKind != JsonValueKind.Object ||
            !trace.TryGetProperty("runId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != run.Evidence.Info.RunId ||
            !trace.TryGetProperty("scenarioId", out var scenario) || scenario.ValueKind != JsonValueKind.String ||
            !trace.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array ||
            !run.Evidence.Results.Any(r => r.GetProperty("caseId").GetString() == scenario.GetString() &&
                r.TryGetProperty("evidence", out var refs) && refs.EnumerateArray().Any(e => e.GetString() == "E-trace")))
            throw new InvalidDataException("The captured trace does not agree with its selected run and scenario.");
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kind.GetString()) ||
                !item.TryGetProperty("timestamp", out var time) || time.ValueKind != JsonValueKind.String ||
                !time.TryGetDateTimeOffset(out _) || !HasOffset(time.GetString()!) ||
                !OptionalText(item, "name") || !OptionalText(item, "payload"))
                throw new InvalidDataException("A captured trace event is missing a recorded timestamp or kind, or has an unsupported text field.");
        }
        return bytes;
    }

    // Legacy filesystem readers accepted BOM-aware text. The viewer requires
    // UTF-8 JSON; transcoding text must not deserialize/rewrite evidence fields.
    // The generic artifact route always retains the exact original bytes.
    private static ReadOnlyMemory<byte> JsonUtf8(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Span.StartsWith("\uFEFF"u8)) return bytes[3..];
        Encoding? encoding = null;
        var offset = 0;
        if (bytes.Span.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (bytes.Span.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (bytes.Span.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.Span.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        if (encoding is null) return bytes;
        try { return Encoding.UTF8.GetBytes(encoding.GetString(bytes.Span[offset..])); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("The captured trace has invalid encoded text.", error); }
    }

    private static bool OptionalText(JsonElement item, string name) => !item.TryGetProperty(name, out var value) ||
        value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool HasOffset(string timestamp) => timestamp.EndsWith('Z') ||
        (timestamp.Length >= 6 && timestamp[^6] is '+' or '-' && timestamp[^3] == ':');
}
