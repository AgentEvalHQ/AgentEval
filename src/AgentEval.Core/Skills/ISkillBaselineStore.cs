// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// Agent Skills Wave 1 (§4.1) — persistence for the multi-snapshot baseline ledger. A SIBLING of
/// <c>AgentEval.Memory.Reporting.JsonFileBaselineStore</c>, not a subtype — that one is hard-typed to
/// <c>MemoryBaseline</c> and does Memory-specific side effects (manifest.json rebuild, report.html/
/// archetypes.json embedded-resource copying) that don't apply here. This interface copies the PROVEN
/// PATTERN (one file per snapshot, never overwritten, <see cref="ListAsync"/> orders oldest→newest), not
/// the type.
/// </summary>
public interface ISkillBaselineStore
{
    /// <summary>Saves a new snapshot. Never overwrites a prior one — returns <see cref="SkillBaselineSnapshot.Id"/>.</summary>
    Task<string> SaveAsync(SkillBaselineSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Loads a snapshot by its <see cref="SkillBaselineSnapshot.Id"/>, or <see langword="null"/> if not found.</summary>
    Task<SkillBaselineSnapshot?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>Every snapshot ever saved, ordered oldest → newest by <see cref="SkillBaselineSnapshot.CapturedAt"/>.</summary>
    Task<IReadOnlyList<SkillBaselineSnapshot>> ListAsync(CancellationToken ct = default);
}
