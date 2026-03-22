// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Persistence interface for memory benchmark baselines.
/// Implementations handle saving, loading, listing, and deleting baselines.
/// </summary>
public interface IBaselineStore
{
    /// <summary>
    /// Saves a baseline snapshot. Implementations may also rebuild manifests
    /// and copy report templates as side effects.
    /// </summary>
    Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default);

    /// <summary>Loads a baseline by its unique ID.</summary>
    Task<MemoryBaseline?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>Lists baselines, optionally filtered by agent name or tags.</summary>
    Task<IReadOnlyList<MemoryBaseline>> ListAsync(
        string? agentName = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default);

    /// <summary>Deletes a baseline by ID. Returns true if found and deleted.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
