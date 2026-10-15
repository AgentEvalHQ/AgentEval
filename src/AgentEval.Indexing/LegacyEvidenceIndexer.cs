// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Ingestion;

namespace AgentEval.Indexing;

/// <summary>A legacy source location and a stable issue code.</summary>
public sealed record LegacyIndexIssue(string RelativePath, string Code);
/// <summary>Coverage and mutations of one bounded legacy discovery/import pass.</summary>
public sealed record LegacyIndexImport(int Candidates, int Processed, int Changed, int Unchanged, int Removed,
    bool Complete, ImmutableArray<LegacyIndexIssue> Issues)
{
    /// <summary>Relative sources successfully upserted or confirmed unchanged by this pass.</summary>
    public ImmutableArray<string> ImportedPaths { get; init; } = [];
}

/// <summary>Discovers and incrementally indexes legacy output-store run folders.</summary>
public interface ILegacyEvidenceIndexer
{
    /// <summary>Imports manifest.json candidates; optional pruning requires a complete successful pass.</summary>
    LegacyIndexImport Import(string rootId, string directory, AefDiscoveryOptions? options = null,
        bool pruneMissing = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reuses bounded marker traversal and guarded index pruning. Candidates are identified by manifest.json;
/// unsupported manifests are reported rather than silently treated as successful coverage.
/// </summary>
public sealed class LegacyEvidenceIndexer(ILegacyIndexSourceReader reader, IEvidenceIndexWriter writer) : ILegacyEvidenceIndexer
{
    /// <inheritdoc />
    public LegacyIndexImport Import(string rootId, string directory, AefDiscoveryOptions? options = null,
        bool pruneMissing = false, CancellationToken cancellationToken = default)
    {
        var result = DirectoryEvidenceIndexImport.Import(rootId, directory, options, pruneMissing, cancellationToken,
            "legacy", "manifest.json", (path, limits, token) => AefRunDiscovery.DiscoverByMarker(path, "manifest.json", limits, token), reader.Read, writer);
        return new(result.Candidates, result.Processed, result.Changed, result.Unchanged, result.Removed, result.Complete,
            result.Issues.Select(i => new LegacyIndexIssue(i.RelativePath, i.Code)).ToImmutableArray())
            { ImportedPaths = result.ImportedPaths };
    }
}
