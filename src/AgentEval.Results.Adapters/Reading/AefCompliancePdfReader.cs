// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Results.Adapters.Reading;

internal sealed record AefCompliancePdf(ComplianceEvidence Evidence, ReadOnlyMemory<byte>? Bytes);

// A source PDF explicitly references a captured compliance document; paths and display names are not identity.
internal static class AefCompliancePdfReader
{
    internal const string ExtensionName = "agenteval.compliance";
    internal static IReadOnlyList<AefCompliancePdf> Read(AefRunEvidence run, CancellationToken ct)
    {
        var result = new List<AefCompliancePdf>();
        foreach (var artifact in run.Evidence)
        {
            ct.ThrowIfCancellationRequested();
            if (!artifact.TryGetProperty("ext", out var ext) || !ext.TryGetProperty(ExtensionName, out var metadata)) continue;
            if (metadata.ValueKind != JsonValueKind.Object) throw Bad("The compliance PDF metadata is not an object.");
            if (!metadata.TryGetProperty("pdfFor", out var reference)) continue;
            if (reference.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reference.GetString())
                || artifact.GetProperty("kind").GetString() != "document") throw Bad("The PDF reference or artifact kind is invalid.");
            var target = run.Evidence.SingleOrDefault(e => e.GetProperty("evidenceId").GetString() == reference.GetString());
            if (target.ValueKind == JsonValueKind.Undefined || target.GetProperty("kind").GetString() != "compliance_artifact"
                || run.CapturedContent(reference.GetString()!) is not { } documentBytes)
                throw Bad("The PDF does not reference a captured compliance document.");
            var document = new AefComplianceArtifactReader().ReadDocument(documentBytes.ToArray(), reference.GetString()!, run.Info.SubjectRef);
            result.Add(new(document.Evidence, run.CapturedContent(artifact.GetProperty("evidenceId").GetString()!)));
        }
        return result;
    }

    internal static ReadOnlyMemory<byte>? Select(IReadOnlyList<AefCompliancePdf> pdfs, ComplianceEvidence evidence)
    {
        var matching = pdfs.Where(p => AefComplianceArtifactReader.SameIdentity(p.Evidence, evidence)).ToArray();
        if (matching.Length > 1) throw Bad("Multiple PDF artifacts claim the same compliance report.");
        return matching.SingleOrDefault()?.Bytes;
    }

    private static InvalidDataException Bad(string actual) => new("Expected: one explicitly linked captured source PDF. Actual: " + actual +
        " Suggestions: inspect the report's evidence references. Because: a filename or an external link cannot establish captured report content.");
}
