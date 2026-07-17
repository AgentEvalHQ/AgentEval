// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// Agent Skills Wave 1 (§4.1) — one skill's snapshot within a <see cref="SkillBaselineSnapshot"/>. Carries
/// TWO independent hashes on purpose (see each field's remarks) — they catch different kinds of drift and
/// neither subsumes the other. Distinct from <see cref="SkillManifestBaseline"/> (that type is a
/// single-pin, Gatekeeper/runtime-adjacent CI-gate baseline — "commit this file, compare a later run
/// against it"); this type is one ENTRY in a historical, multi-snapshot, scan-verb-adjacent LEDGER. Do not
/// conflate the two — see <see cref="SkillBaselineSnapshot"/> remarks.
/// </summary>
/// <param name="Name">Skill name (matches <see cref="SkillManifest.Name"/>).</param>
/// <param name="SourceKind"><see cref="SkillManifest.SourceKind"/> at scan time.</param>
/// <param name="RelativePath">Path within the scanned root (file-sourced skills only; empty for non-file sources).</param>
/// <param name="StructuralFingerprint">
/// <see cref="SkillManifestPoisoningGate.Fingerprint(SkillManifest)"/> — REUSED, not recomputed: catches
/// name/description/resource-list/script-list/allowed-tools/compatibility-length changes. Cheap (no file
/// I/O beyond what the scan already did).
/// </param>
/// <param name="ContentHash">
/// <see cref="SkillContentHasher.HashSkillFolder"/> — full file-CONTENT hash. Catches a resource/script
/// FILE'S CONTENT changing even when its name and the frontmatter don't (<see cref="StructuralFingerprint"/>
/// alone cannot see this — it only hashes resource/script NAMES, not bytes). <see langword="null"/> for
/// non-file-sourced skills (no folder to hash).
/// </param>
/// <param name="Source">From a discovered <c>skills-lock.json</c> if present, else <see langword="null"/> — not required, most repos won't have one. No lock-file parser exists yet in Wave 1; this field is wired end-to-end (store → renderer) but always <see langword="null"/> until Wave 2/3 populates it from a real source.</param>
/// <param name="SourceUrl">As <see cref="Source"/>.</param>
/// <param name="Ref">Git ref/commit, if known from a discovered lock file. As <see cref="Source"/>.</param>
/// <param name="ComplianceFindings">This skill's findings, filtered from the scan's <see cref="SkillComplianceReport.Findings"/> by <see cref="SkillComplianceFinding.SkillName"/> — <see cref="SkillComplianceReport"/> is flat, no per-skill grouping type exists, so this is a client-side filter, not a reuse of an existing per-skill type.</param>
public sealed record SkillBaselineEntry(
    string Name,
    SkillSourceKind SourceKind,
    string RelativePath,
    string StructuralFingerprint,
    string? ContentHash,
    string? Source,
    string? SourceUrl,
    string? Ref,
    IReadOnlyList<SkillComplianceFinding> ComplianceFindings);

/// <summary>
/// Agent Skills Wave 1 (§4.1) — a timestamped, content-hashed snapshot of every skill scanned at once.
/// Historical (many snapshots accumulate over time via <see cref="ISkillBaselineStore"/>, one JSON file
/// each, never overwritten) — deliberately NOT the same shape as <see cref="SkillManifestBaseline"/>'s
/// single-pin CI-gate baseline. See that type's remarks and <see cref="SkillBaselineEntry"/>'s remarks for
/// the full disambiguation — two very-similarly-purposed but structurally different types live in this
/// namespace on purpose.
/// </summary>
/// <param name="Id">A ULID/GUID, not a timestamp-derived slug — timestamps alone can collide within a second on fast test/CI runs.</param>
/// <param name="CapturedAt">When this snapshot was taken.</param>
/// <param name="ScannedRoot">The root path that was scanned to produce this snapshot.</param>
/// <param name="Skills">One entry per skill scanned.</param>
public sealed record SkillBaselineSnapshot(
    string Id,
    DateTimeOffset CapturedAt,
    string ScannedRoot,
    IReadOnlyList<SkillBaselineEntry> Skills);
