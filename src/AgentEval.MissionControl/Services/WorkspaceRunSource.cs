// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.MissionControl.Services;

/// <summary>Read-only evidence access, separate from solution and subject inventory configuration.</summary>
public interface IWorkspaceRunSource
{
    /// <summary>Loads a complete bounded catalog; invalid or partial evidence throws.</summary>
    Task<IReadOnlyList<AefWorkspaceRun>> ReadAsync(CancellationToken ct);
}

/// <summary>Reads exact filesystem sources through AEF; injected semantic stores are converter inputs only.</summary>
public sealed class WorkspaceRunSource(IOutputStoreReader store, IAefWorkspaceEvidenceReader reader, long maxBytes = 256L << 20) : IWorkspaceRunSource
{
    // Scoped to a request/resolver, never a process cache that could hide a changed source.
    private Task<IReadOnlyList<AefWorkspaceRun>>? _read;
    private readonly object _sync = new();
    public Task<IReadOnlyList<AefWorkspaceRun>> ReadAsync(CancellationToken ct)
    {
        lock (_sync) return _read ??= LoadAsync(ct);
    }

    private async Task<IReadOnlyList<AefWorkspaceRun>> LoadAsync(CancellationToken ct) =>
        (await reader.ReadAsync(store, maxBytes, ct)).Runs;

    internal static AefWorkspaceRun? Find(IReadOnlyList<AefWorkspaceRun> runs, string id, string? sourceId)
    {
        var matches = runs.Where(r => (sourceId is null || r.SourceId == sourceId)
            && (r.Evidence.Info.RunId == id || r.RunAliases.Contains(id, StringComparer.Ordinal))).ToArray();
        if (matches.Length > 1) throw Bad("The requested run ID is ambiguous; select its sourceId.");
        return matches.SingleOrDefault();
    }

    private static InvalidDataException Bad(string actual) => new("Expected: complete unambiguous workspace evidence. Actual: " + actual +
        " Suggestions: select an exact bounded source. Because: the dashboard must not silently choose or truncate evidence.");
}
