// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>Finds candidate native evidence, including damaged runs missing their header. Candidates still require verification.</summary>
public static class AefEvidenceDiscovery
{
    /// <summary>
    /// Performs bounded marker scans without following links. Missing roots return empty; incomplete scans throw.
    /// The bounds apply to each of four scans and the combined candidate count. Results contain local paths for trusted callers only.
    /// </summary>
    public static IReadOnlyList<string> Discover(string root, AefDiscoveryOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        // Validate even for an absent root, before accepting any caller-supplied budget.
        if (options.MaxDepth is < 0 or > 64 || options.MaxEntries is < 1 or > 1_000_000 || options.MaxRuns is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: bounded positive discovery limits. Actual: invalid bounds. " +
                "Suggestions: use the default limits. Because: missing roots must not bypass budget validation.");
        root = Path.GetFullPath(root);
        ct.ThrowIfCancellationRequested();
        if (!Path.Exists(root)) return [];
        var directories = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var marker in new[] { "run.json", "results.ndjson", "seal.json", "metrics.json" })
        {
            var discovered = AefRunDiscovery.DiscoverByMarker(root, marker, options, ct);
            if (!discovered.Complete) throw Bad("The native evidence scan was incomplete: " + string.Join(", ", discovered.Issues.Select(i => i.Kind)));
            directories.UnionWith(discovered.Candidates.Select(c => c.Directory));
            if (directories.Count > options.MaxRuns) throw Bad("The combined native candidate count exceeds the configured limit.");
        }
        return Array.AsReadOnly(directories.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    private static InvalidDataException Bad(string actual) => new("Expected: complete bounded native evidence discovery. Actual: " + actual +
        " Suggestions: narrow the authorized source or correct unreadable evidence. Because: partial discovery must not appear to be an empty or complete workspace.");
}
