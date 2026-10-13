// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;

namespace AgentEval.Results.Ingestion;

/// <summary>
/// Bounded, non-recursive-stack discovery of candidate AEF folders. Directory enumeration includes hidden
/// entries, follows no links, and stops descending at a run.json marker. No file contents are opened.
/// </summary>
/// <remarks>
/// A filesystem scan is not an atomic snapshot. Ingestion must copy and verify candidates before accepting
/// them. Source-directory ownership must prevent hostile concurrent path replacement. Partial selection
/// follows filesystem enumeration order; only the returned candidate and issue lists are sorted.
/// </remarks>
public sealed class AefRunDiscovery : IAefRunDiscovery
{
    /// <inheritdoc />
    public AefDiscoveryResult Discover(string root, AefDiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Expected: an absolute discovery root. Actual: missing or relative root. " +
                "Suggestions: supply an explicit source directory. Because: discovery must not infer filesystem scope.", nameof(root));
        options ??= new();
        if (options.MaxDepth is < 0 or > 64 || options.MaxEntries is < 1 or > 1_000_000 || options.MaxRuns is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: depth 0..64, entries 1..1000000, runs 1..100000. " +
                "Actual: an out-of-range discovery limit. Suggestions: use bounded positive budgets. Because: scanning must remain bounded.");
        cancellationToken.ThrowIfCancellationRequested();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (AefFolder.KindOf(ancestor) != AefEntryKind.Folder)
                throw new IOException("Expected: ordinary source-directory ancestry. Actual: a non-directory or link. " +
                    "Suggestions: select an existing root without linked components. Because: discovery must not follow links.");
        }

        var candidates = new List<AefRunCandidate>();
        var issues = new List<AefDiscoveryIssue>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        var inspected = 0;
        var stop = false;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

        while (!stop && pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = new List<string>();
            var candidate = false;
            try
            {
                // Recheck a queued directory before entering it, since it may have changed since listing.
                var currentKind = AefFolder.KindOf(current.Directory);
                if (currentKind != AefEntryKind.Folder)
                {
                    issues.Add(new(Relative(current.Directory), currentKind == AefEntryKind.Link
                        ? AefDiscoveryIssueKind.LinkSkipped : AefDiscoveryIssueKind.Unreadable));
                    continue;
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Directory, "*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inspected == options.MaxEntries)
                    {
                        issues.Add(new(Relative(current.Directory), AefDiscoveryIssueKind.EntryLimit));
                        stop = true;
                        break;
                    }
                    inspected++;
                    try
                    {
                        switch (AefFolder.KindOf(entry))
                        {
                            case AefEntryKind.Folder: children.Add(entry); break;
                            case AefEntryKind.File:
                                if (Path.GetFileName(entry) == "run.json") candidate = true;
                                break;
                            case AefEntryKind.Link: issues.Add(new(Relative(entry), AefDiscoveryIssueKind.LinkSkipped)); break;
                            default: issues.Add(new(Relative(entry), AefDiscoveryIssueKind.SpecialEntrySkipped)); break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new(Relative(entry), AefDiscoveryIssueKind.Unreadable));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(Relative(current.Directory), AefDiscoveryIssueKind.Unreadable));
            }

            if (candidate)
            {
                if (candidates.Count == options.MaxRuns)
                {
                    issues.Add(new(Relative(current.Directory), AefDiscoveryIssueKind.RunLimit));
                    stop = true;
                }
                else candidates.Add(new(current.Directory));
                // A run can contain extension folders with their own run.json. They are not separate imports.
                continue;
            }
            if (stop) continue;
            foreach (var child in children)
            {
                if (current.Depth == options.MaxDepth) issues.Add(new(Relative(child), AefDiscoveryIssueKind.DepthLimit));
                else pending.Push((child, current.Depth + 1));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(root, candidates, issues, inspected);
    }
}
