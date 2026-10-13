// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Ingestion;

/// <summary>Finds candidate run folders without opening evidence files or accepting them into storage.</summary>
public interface IAefRunDiscovery
{
    /// <summary>Scans an explicit local source root. Cancellation throws instead of returning a successful partial scan.</summary>
    AefDiscoveryResult Discover(string root, AefDiscoveryOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Bounds on discovery, independent of the AEF verifier's limits on a single run.</summary>
public sealed class AefDiscoveryOptions
{
    /// <summary>Maximum child-directory depth, with the source root at zero. Range 0..64; default 8.</summary>
    public int MaxDepth { get; init; } = 8;
    /// <summary>Maximum entries inspected across the scan. Range 1..1,000,000; default 10,000.</summary>
    public int MaxEntries { get; init; } = 10_000;
    /// <summary>Maximum candidates returned. Range 1..100,000; default 1,000.</summary>
    public int MaxRuns { get; init; } = 1_000;
}

/// <summary>A folder with a regular run.json marker. Its contents, seal and identity have not been verified.</summary>
/// <param name="Directory">Absolute local directory, never an HTTP upload destination.</param>
public sealed record AefRunCandidate(string Directory);

/// <summary>A reason discovery could not cover a source tree completely.</summary>
public enum AefDiscoveryIssueKind
{
    /// <summary>A child directory exceeds the permitted depth.</summary>
    DepthLimit,
    /// <summary>More entries exist than the scan budget permits inspecting.</summary>
    EntryLimit,
    /// <summary>More candidates were found than the result budget permits returning.</summary>
    RunLimit,
    /// <summary>An entry or directory was unavailable or inaccessible.</summary>
    Unreadable,
    /// <summary>A symbolic link or reparse point was not followed.</summary>
    LinkSkipped,
    /// <summary>A pipe, socket or device was not opened.</summary>
    SpecialEntrySkipped
}

/// <summary>An omitted part of a scan, with a path relative to the configured source root.</summary>
public sealed record AefDiscoveryIssue(string Path, AefDiscoveryIssueKind Kind);

/// <summary>An immutable scan snapshot. Complete describes discovery coverage, never AEF validity.</summary>
public sealed class AefDiscoveryResult
{
    internal AefDiscoveryResult(string root, IEnumerable<AefRunCandidate> candidates,
        IEnumerable<AefDiscoveryIssue> issues, int inspectedEntries)
    {
        Root = root;
        Candidates = Array.AsReadOnly(candidates.OrderBy(c => c.Directory, StringComparer.Ordinal).ToArray());
        Issues = Array.AsReadOnly(issues.OrderBy(i => i.Path, StringComparer.Ordinal).ThenBy(i => i.Kind).ToArray());
        InspectedEntries = inspectedEntries;
    }

    /// <summary>The normalized explicit source root.</summary>
    public string Root { get; }
    /// <summary>Unverified candidates, sorted ordinally by directory.</summary>
    public IReadOnlyList<AefRunCandidate> Candidates { get; }
    /// <summary>Omissions and bounds reached during this scan.</summary>
    public IReadOnlyList<AefDiscoveryIssue> Issues { get; }
    /// <summary>Entries inspected, excluding the root and at most one limit-detection lookahead entry.</summary>
    public int InspectedEntries { get; }
    /// <summary>True only when discovery encountered no omissions. Candidate contents are not traversed.</summary>
    public bool Complete => Issues.Count == 0;
}
