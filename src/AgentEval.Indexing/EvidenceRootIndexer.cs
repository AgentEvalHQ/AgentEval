// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Ingestion;

namespace AgentEval.Indexing;

/// <summary>One configured root's import coverage and mutations.</summary>
public sealed record EvidenceRootIndexResult(string RootId, int Changed, int Unchanged, int Removed, bool Complete);
/// <summary>A source issue names its configured namespace and relative location.</summary>
public sealed record EvidenceRootIndexIssue(string RootId, string RelativePath, string Code);
/// <summary>A bounded root plan, import results and coverage issues; individual sources commit independently.</summary>
public sealed record EvidenceRootIndexImport(EvidenceIndexRootPlan Plan, ImmutableArray<EvidenceRootIndexResult> Roots,
    ImmutableArray<EvidenceRootIndexIssue> Issues)
{
    /// <summary>True only when every scan and covered-namespace reconciliation completed successfully.</summary>
    public bool Complete => Issues.IsEmpty && Roots.All(r => r.Complete);
}

/// <summary>Indexes configured AEF/legacy roots and reconciles covered namespaces after successful scans.</summary>
public interface IEvidenceRootIndexer
{
    /// <summary>Root IDs must retain their directory/format binding. No evidence files are modified.</summary>
    EvidenceRootIndexImport Import(IEnumerable<EvidenceIndexRoot> roots, AefDiscoveryOptions? options = null,
        bool pruneMissing = false, int maximumRoots = 256, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the explicit outermost-root plan for evaluated-run formats. Observation stores have their own
/// inventory contract and are imported separately. A failed owner scan never retires a covered namespace.
/// </summary>
public sealed class EvidenceRootIndexer(IEvidenceIndexRootPlanner planner, IAefEvidenceIndexer aef,
    ILegacyEvidenceIndexer legacy, IEvidenceIndexWriter writer) : IEvidenceRootIndexer
{
    /// <inheritdoc />
    public EvidenceRootIndexImport Import(IEnumerable<EvidenceIndexRoot> roots, AefDiscoveryOptions? options = null,
        bool pruneMissing = false, int maximumRoots = 256, CancellationToken cancellationToken = default)
    {
        var plan = planner.Plan(roots, maximumRoots, cancellationToken);
        if (plan.ScanRoots.Any(r => r.Kind == IndexedEvidenceKind.Observation))
            throw new EvidenceIndexException("unsupported-root-format");
        writer.BindRoots(plan.ScanRoots.Concat(plan.CoveredRoots.Select(r => r.Root)), cancellationToken);
        // Capture guards before any import mutation. Reconciliation only touches these prior rows.
        var previous = plan.CoveredRoots.ToDictionary(r => r.Root.RootId,
            r => writer.ListSources(r.Root.RootId, cancellationToken), StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<EvidenceRootIndexResult>();
        var issues = ImmutableArray.CreateBuilder<EvidenceRootIndexIssue>();
        var imported = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var root in plan.ScanRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (root.Kind == IndexedEvidenceKind.Aef)
                {
                    var scan = aef.Import(root.RootId, root.Directory, options, pruneMissing, cancellationToken);
                    results.Add(new(root.RootId, scan.Changed, scan.Unchanged, scan.Removed, scan.Complete));
                    imported.Add(root.RootId, scan.ImportedPaths.ToHashSet(StringComparer.Ordinal));
                    foreach (var issue in scan.Issues) issues.Add(new(root.RootId, issue.RelativePath, issue.Code));
                }
                else
                {
                    var scan = legacy.Import(root.RootId, root.Directory, options, pruneMissing, cancellationToken);
                    results.Add(new(root.RootId, scan.Changed, scan.Unchanged, scan.Removed, scan.Complete));
                    imported.Add(root.RootId, scan.ImportedPaths.ToHashSet(StringComparer.Ordinal));
                    foreach (var issue in scan.Issues) issues.Add(new(root.RootId, issue.RelativePath, issue.Code));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results.Add(new(root.RootId, 0, 0, 0, false));
                issues.Add(new(root.RootId, ".", error is EvidenceIndexException indexError ? indexError.Code : "source-unavailable"));
            }
        }
        foreach (var covered in plan.CoveredRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = plan.ScanRoots.Single(r => r.RootId == covered.CoveringRootId);
            var ownerResult = results.Single(r => r.RootId == owner.RootId);
            var complete = ownerResult.Complete;
            var removed = 0;
            if (!complete) issues.Add(new(covered.Root.RootId, ".", "covering-scan-incomplete"));
            else foreach (var old in previous[covered.Root.RootId])
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var kind = owner.Kind == IndexedEvidenceKind.Aef ? "aef" : "legacy";
                    if (old.Checkpoint.Kind != kind) throw new EvidenceIndexException("root-format-conflict");
                    var sourceDirectory = Path.GetFullPath(Path.Combine(covered.Root.Directory, old.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    var ownerRelative = Path.GetRelativePath(owner.Directory, sourceDirectory).Replace(Path.DirectorySeparatorChar, '/');
                    var replacement = imported[owner.RootId].Contains(ownerRelative)
                        ? writer.Find(owner.RootId, ownerRelative, cancellationToken) : null;
                    if (replacement is null)
                    {
                        // A stop-at-marker scan may not reach a formerly explicit nested run. Never silently
                        // discard it: absence must be independently established, and pruning must be opted in.
                        if (!pruneMissing) throw new EvidenceIndexException("covered-source-not-imported");
                        var marker = Path.Combine(sourceDirectory, kind == "aef" ? "run.json" : "manifest.json");
                        try { _ = File.GetAttributes(marker); throw new EvidenceIndexException("covered-source-not-imported"); }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                    else if (replacement.Kind != kind) throw new EvidenceIndexException("root-format-conflict");
                    if (writer.Remove(covered.Root.RootId, old.RelativePath, old.Checkpoint.Fingerprint, cancellationToken).Changed) removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    issues.Add(new(covered.Root.RootId, old.RelativePath, error is EvidenceIndexException indexError ? indexError.Code : "source-unavailable"));
                }
            }
            results.Add(new(covered.Root.RootId, 0, 0, removed, complete));
        }
        return new(plan, results.ToImmutable(), issues.ToImmutable());
    }
}
