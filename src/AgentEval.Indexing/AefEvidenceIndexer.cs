// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Ingestion;

namespace AgentEval.Indexing;

/// <summary>A relative source location and stable issue code, without an absolute filesystem path.</summary>
public sealed record AefIndexIssue(string RelativePath, string Code);
/// <summary>Coverage of a bounded AEF discovery/import pass, never a deletion authorization or benchmark completeness claim.</summary>
public sealed record AefIndexImport(int Candidates, int Processed, int Changed, int Unchanged, int Removed, bool Complete,
    ImmutableArray<AefIndexIssue> Issues)
{
    /// <summary>Relative sources successfully upserted or confirmed unchanged by this pass.</summary>
    public ImmutableArray<string> ImportedPaths { get; init; } = [];
}
/// <summary>Coordinates bounded discovery, verified snapshot projection and transactional source updates.</summary>
public interface IAefEvidenceIndexer
{
    /// <summary>Imports candidates. Optional pruning requires a complete successful pass and rechecks absent markers before guarded deletion.</summary>
    AefIndexImport Import(string rootId, string directory, AefDiscoveryOptions? options = null, bool pruneMissing = false, CancellationToken cancellationToken = default);
}
/// <summary>Initial/incremental AEF indexing through shared discovery and immutable snapshot services.</summary>
public sealed class AefEvidenceIndexer(IAefRunDiscovery discovery, IAefIndexSourceReader reader, IEvidenceIndexWriter writer) : IAefEvidenceIndexer
{
    /// <inheritdoc />
    public AefIndexImport Import(string rootId, string directory, AefDiscoveryOptions? options = null, bool pruneMissing = false, CancellationToken cancellationToken = default)
    {
        return DirectoryEvidenceIndexImport.Import(rootId, directory, options, pruneMissing, cancellationToken,
            "aef", "run.json", discovery.Discover, reader.Read, writer);
    }
}
