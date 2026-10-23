// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Reading;

// Identity parsing only. The CLI reporting assembly validates its complete schema before typed rendering.
internal sealed record AgenticReportDocument(JsonElement Document, string SubjectRef, AefReportArtifact Artifact)
{
    internal string Timestamp => Artifact.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

    internal static AgenticReportDocument Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            AefJsonReader.Check(bytes.Span, 64 << 20);
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 128 });
            var root = json.RootElement;
            foreach (var name in new[] { "compositeTree", "summary", "attestation", "subject" })
                if (root.GetProperty(name).ValueKind != JsonValueKind.Object) throw Bad("The report omits a required object.");
            foreach (var name in new[] { "criticalFindings", "recommendations" })
                if (root.GetProperty(name).ValueKind != JsonValueKind.Array) throw Bad("The report omits a required array.");
            Text(root, "preset");
            if (root.GetProperty("disclaimer").ValueKind != JsonValueKind.String)
                throw Bad("The report disclaimer is missing or invalid.");
            var subject = root.GetProperty("subject");
            var kind = Text(subject, "kind");
            if (kind is not ("agent" or "workflow")) throw Bad("The report subject kind is unsupported.");
            var artifact = new AefReportArtifact
            {
                Type = "agentic-result", SourceRunId = Text(root, "sourceRunId"),
                SourceManifestHash = Text(root, "sourceManifestHash"),
                GeneratedAt = root.GetProperty("generatedAt").GetDateTimeOffset(), Bytes = bytes,
            };
            artifact.Metadata(AefContentCapture.On);
            return new(root.Clone(), AefConverter.TypedRef(kind, Text(subject, "name")), artifact);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or AefEncodingException)
        { throw Bad("The original report has missing, ambiguous or invalid identity/structure.", error); }
    }

    internal static string Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && AefConverter.IsResultText(text, 4096)
            ? text : throw Bad("A required report identity string is missing or invalid: " + name);

    internal static InvalidDataException Bad(string actual, Exception? inner = null) => new(
        "Expected: an unambiguous recorded agentic report from verified evidence. Actual: " + actual +
        " Suggestions: select the original valid report. Because: missing content or identity must not be reconstructed from a legacy sibling.", inner);
}
