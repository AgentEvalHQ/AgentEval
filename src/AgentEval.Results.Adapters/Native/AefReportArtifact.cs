// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>A producer's original report document and its recorded identity, independent of execution custody.</summary>
public sealed record AefReportArtifact
{
    /// <summary>The report format identity, for example agentic-result.</summary>
    public required string Type { get; init; }
    /// <summary>The report's recorded generation time.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }
    /// <summary>The original run reference recorded by the reporter.</summary>
    public required string SourceRunId { get; init; }
    /// <summary>The original manifest reference recorded by the reporter; not an AEF seal.</summary>
    public required string SourceManifestHash { get; init; }
    /// <summary>Exact original document bytes. May be empty only when content capture is off.</summary>
    public required ReadOnlyMemory<byte> Bytes { get; init; }

    internal const string Extension = "agenteval.report";
    internal const string EvidenceId = "E-report-original";

    internal JsonObject Metadata(AefContentCapture capture)
    {
        if (!AefConverter.IsId(Type) || !AefConverter.IsResultText(SourceRunId, 256)
            || !AefConverter.IsResultText(SourceManifestHash, 256) || GeneratedAt == default
            || Bytes.Length > 64 << 20 || capture == AefContentCapture.On && Bytes.IsEmpty)
            throw new ArgumentException("Expected: a bounded original report with recorded identity. Actual: invalid report metadata or bytes. Suggestions: supply the original reporter output. Because: captured documents must not invent source identity.");
        return new()
        {
            ["version"] = 1, ["type"] = Type, ["generatedAt"] = GeneratedAt.ToString("O", CultureInfo.InvariantCulture),
            ["sourceRunId"] = SourceRunId, ["sourceManifestHash"] = SourceManifestHash,
            ["captured"] = capture == AefContentCapture.On, ["evidenceId"] = EvidenceId,
        };
    }

    internal void Write(AefRunWriter writer, AefContentCapture capture)
    {
        if (capture == AefContentCapture.Off) return;
        writer.AddEvidence(new()
        {
            EvidenceId = EvidenceId, Kind = AefEvidenceKind.Document,
            Link = AefEvidenceLink.ToBlob(writer.PutBlob(Bytes.Span)),
            Description = "Original producer report document. Its source reference does not establish original execution custody.",
        });
    }
}
