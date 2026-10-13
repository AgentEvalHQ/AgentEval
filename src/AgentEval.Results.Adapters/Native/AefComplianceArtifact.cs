// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>A reporter's exact bytes and the observed cases whose controls it describes.</summary>
public sealed record AefComplianceArtifact
{
    /// <summary>The original artifact bytes; no reserialization is performed.</summary>
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    /// <summary>Existing root case IDs that cite the artifact. At least one is required.</summary>
    public required IReadOnlyList<string> CaseIds { get; init; }
    /// <summary>Defaults to true. Only a producer that establishes content-free bytes may set false.</summary>
    public bool ContainsContent { get; init; } = true;

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Write(AefRunWriter writer,
        IReadOnlyList<AefComplianceArtifact>? artifacts, AefContentCapture capture, IEnumerable<string> caseIds)
    {
        var known = caseIds.ToHashSet(StringComparer.Ordinal);
        var links = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts ?? [])
        {
            if (artifact.ContainsContent && capture != AefContentCapture.On) continue;
            if (artifact.CaseIds.Count == 0 || artifact.CaseIds.Any(id => !known.Contains(id)))
                throw new AefInputException("A compliance artifact must cite existing root cases.");
            var blob = writer.PutBlob(artifact.Bytes.Span);
            var id = "E-" + blob.Digest["sha256:".Length..];
            if (written.Add(id)) writer.AddEvidence(new AefEvidence
            {
                EvidenceId = id, Kind = AefEvidenceKind.ComplianceArtifact, Link = AefEvidenceLink.ToBlob(blob),
            });
            foreach (var caseId in artifact.CaseIds.Distinct(StringComparer.Ordinal))
            {
                if (!links.TryGetValue(caseId, out var ids)) links.Add(caseId, ids = []);
                if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
            }
        }
        return links.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }
}
