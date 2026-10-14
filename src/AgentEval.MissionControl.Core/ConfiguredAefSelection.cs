// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;

namespace AgentEval.MissionControl.Core;

// Server-side only: never return this location model through a transport.
internal sealed record ResolvedAefSelection(EvidenceSourceSelection Selection, string Directory, string RunHash);
internal sealed class ConfiguredAefSelection
{
    private readonly IEvidenceSourceLookup _lookup;
    private readonly Dictionary<string, EvidenceIndexRoot> _roots;
    internal ConfiguredAefSelection(IEvidenceSourceLookup lookup, IEnumerable<EvidenceIndexRoot> roots)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        var plan = new EvidenceIndexRootPlanner().Plan(roots);
        _roots = plan.ScanRoots.Concat(plan.CoveredRoots.Select(r => r.Root)).ToDictionary(r => r.RootId, StringComparer.Ordinal);
    }
    internal QueryResult<ResolvedAefSelection> Resolve(string sourceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selected = _lookup.Find(sourceId, token);
        if (selected is null) return new(QueryState.NotFound, null, "source-not-found");
        if (!_roots.TryGetValue(selected.Source.RootId, out var root)) return new(QueryState.NotFound, null, "source-not-found");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (selected.Binding is not { } binding || binding.Kind != root.Kind ||
            !string.Equals(binding.Directory, root.Directory, comparison)) return new(QueryState.Unavailable, null, "root-binding-mismatch");
        if (selected.Source.Kind != "aef" || root.Kind != IndexedEvidenceKind.Aef)
            return new(QueryState.InvalidRequest, null, "wrong-source-kind");
        if (selected.Source.Verification != "intact" || selected.Run?.RunHash is not { Length: 64 } hash)
            return new(QueryState.Unavailable, null, "sealed-evidence-required");
        var relative = selected.Source.RelativePath;
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || relative.Contains('\\') || relative.Contains(':') ||
            relative.StartsWith('/') || relative.Any(char.IsControl) || relative.Split('/').Any(p => p is "" or ".." || p == "." && relative != "."))
            return new(QueryState.Unavailable, null, "invalid-source-location");
        var directory = Path.GetFullPath(Path.Combine(root.Directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        // Recheck ordinary ancestors at use time, including the selected run directory. The verifier
        // independently checks evidence entries; trusted local storage ownership is still required.
        new EvidenceIndexRootPlanner().Plan([root with { Directory = directory }], cancellationToken: token);
        return new(QueryState.Available, new(selected, directory, hash), null);
    }
}
