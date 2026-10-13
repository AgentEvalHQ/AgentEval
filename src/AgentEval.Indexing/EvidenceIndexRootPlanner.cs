// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Runs;

namespace AgentEval.Indexing;

/// <summary>A stable namespace bound by configuration to an absolute evidence directory and format.</summary>
public sealed record EvidenceIndexRoot(string RootId, string Directory, IndexedEvidenceKind Kind);
/// <summary>A configured root covered by an outer root of the same format.</summary>
public sealed record CoveredEvidenceIndexRoot(EvidenceIndexRoot Root, string CoveringRootId);
/// <summary>Normalized scan roots and explicit coverage decisions. Does not mutate index rows.</summary>
public sealed record EvidenceIndexRootPlan(ImmutableArray<EvidenceIndexRoot> ScanRoots,
    ImmutableArray<CoveredEvidenceIndexRoot> CoveredRoots);

/// <summary>Plans bounded, nonduplicating traversal of configured evidence roots.</summary>
public interface IEvidenceIndexRootPlanner
{
    /// <summary>Validates ordinary directories and selects outermost roots per evidence format.</summary>
    EvidenceIndexRootPlan Plan(IEnumerable<EvidenceIndexRoot> roots, int maximumRoots = 256,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outermost roots own traversal. Equal-directory aliases use the ordinally smallest root ID.
/// Different formats remain separate. Configuration changes require reconciliation of old namespaces;
/// this plan alone does not make existing rows in covered namespaces disappear.
/// </summary>
public sealed class EvidenceIndexRootPlanner : IEvidenceIndexRootPlanner
{
    /// <inheritdoc />
    public EvidenceIndexRootPlan Plan(IEnumerable<EvidenceIndexRoot> roots, int maximumRoots = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (maximumRoots is < 1 or > 4096) throw new EvidenceIndexException("invalid-root-limit");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse)
            throw new EvidenceIndexException("unsupported-file-classifier");
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var validated = new List<EvidenceIndexRoot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (validated.Count == maximumRoots) throw new EvidenceIndexException("root-limit");
            if (root is null || string.IsNullOrWhiteSpace(root.RootId) || root.RootId.Length > 1024 ||
                root.RootId.Any(char.IsControl) || !Enum.IsDefined(root.Kind))
                throw new EvidenceIndexException("invalid-root");
            if (!ids.Add(root.RootId)) throw new EvidenceIndexException("duplicate-root-id");
            if (string.IsNullOrWhiteSpace(root.Directory) || !Path.IsPathFullyQualified(root.Directory))
                throw new EvidenceIndexException("invalid-source-path");
            string directory;
            try { directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Directory)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new EvidenceIndexException("invalid-source-path"); }
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (AefFolder.KindOf(current) != AefEntryKind.Folder)
                        throw new EvidenceIndexException("unsafe-source-path");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new EvidenceIndexException("unsafe-source-path"); }
            }
            validated.Add(root with { Directory = directory });
        }
        var scans = ImmutableArray.CreateBuilder<EvidenceIndexRoot>();
        var covered = ImmutableArray.CreateBuilder<CoveredEvidenceIndexRoot>();
        foreach (var root in validated.OrderBy(r => r.Directory.Length).ThenBy(r => r.RootId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = scans.FirstOrDefault(candidate => candidate.Kind == root.Kind &&
                (paths.Equals(candidate.Directory, root.Directory) || root.Directory.StartsWith(
                    Path.EndsInDirectorySeparator(candidate.Directory) ? candidate.Directory : candidate.Directory + Path.DirectorySeparatorChar,
                    comparison)));
            if (owner is null) scans.Add(root);
            else covered.Add(new(root, owner.RootId));
        }
        return new(scans.OrderBy(r => r.RootId, StringComparer.Ordinal).ToImmutableArray(),
            covered.OrderBy(r => r.Root.RootId, StringComparer.Ordinal).ToImmutableArray());
    }
}
