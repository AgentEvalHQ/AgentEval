// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Ingestion;

namespace AgentEval.Indexing;

// One import/pruning algorithm for both marker-based evaluated-run formats.
internal static class DirectoryEvidenceIndexImport
{
    internal static AefIndexImport Import(string rootId, string directory, AefDiscoveryOptions? options,
        bool pruneMissing, CancellationToken cancellationToken, string kind, string markerName,
        Func<string, AefDiscoveryOptions?, CancellationToken, AefDiscoveryResult> discover,
        Func<string, string, string, CancellationToken, EvidenceIndexSource> read, IEvidenceIndexWriter writer)
    {
        writer.Find(rootId, ".", cancellationToken);
        var previous = pruneMissing ? writer.ListSources(rootId, cancellationToken) : [];
        var inventory = discover(directory, options, cancellationToken);
        var issues = ImmutableArray.CreateBuilder<AefIndexIssue>();
        var imported = ImmutableArray.CreateBuilder<string>();
        foreach (var issue in inventory.Issues) issues.Add(new(issue.Path, "discovery-" + issue.Kind));
        var processed = 0; var changed = 0; var unchanged = 0;
        foreach (var candidate in inventory.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(inventory.Root, candidate.Directory).Replace(Path.DirectorySeparatorChar, '/');
            var stop = false;
            try
            {
                var source = read(rootId, relative, candidate.Directory, cancellationToken);
                var result = writer.Upsert(source, cancellationToken);
                imported.Add(relative);
                if (result.Changed) changed++; else unchanged++;
            }
            catch (EvidenceIndexException error)
            {
                issues.Add(new(relative, error.Code));
                stop = error.Code is "source-limit" or "total-row-limit" or "size-limit" or "busy";
            }
            catch (IOException) { issues.Add(new(relative, "source-unavailable")); }
            catch (UnauthorizedAccessException) { issues.Add(new(relative, "source-unavailable")); }
            processed++;
            if (stop) break;
        }
        var removed = 0;
        if (pruneMissing && inventory.Complete && issues.Count == 0 && processed == inventory.Candidates.Count)
        {
            var present = inventory.Candidates.Select(c => Path.GetRelativePath(inventory.Root, c.Directory).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
            foreach (var old in previous.Where(p => p.Checkpoint.Kind == kind && !present.Contains(p.RelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = Path.Combine(inventory.Root, old.RelativePath.Replace('/', Path.DirectorySeparatorChar), markerName);
                try
                {
                    // GetAttributes distinguishes true absence from permission/I/O failure. File.Exists would
                    // turn an unreadable marker into false and could wrongly authorize pruning.
                    try { _ = File.GetAttributes(marker); issues.Add(new(old.RelativePath, "source-reappeared")); continue; }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    if (writer.Remove(rootId, old.RelativePath, old.Checkpoint.Fingerprint, cancellationToken).Changed) removed++;
                }
                catch (EvidenceIndexException error) { issues.Add(new(old.RelativePath, error.Code)); }
                catch (IOException) { issues.Add(new(old.RelativePath, "source-unavailable")); }
                catch (UnauthorizedAccessException) { issues.Add(new(old.RelativePath, "source-unavailable")); }
            }
        }
        return new(inventory.Candidates.Count, processed, changed, unchanged, removed,
            inventory.Complete && processed == inventory.Candidates.Count && issues.Count == 0, issues.ToImmutable())
            { ImportedPaths = imported.ToImmutable() };
    }
}
