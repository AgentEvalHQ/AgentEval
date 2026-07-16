// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Guardrails;

/// <summary>
/// Deterministic, MAF-free hash-pin-and-diff primitive for "trust-time" drift detection over any
/// model-visible artifact whose DEFINITION is untrusted, attacker-influenceable text — a skill's
/// SKILL.md manifest (Skills Phase 4b, <see cref="AgentEval.Skills.SkillManifestPoisoningGate"/>) or an
/// MCP tool's schema/description (a rug-pull description swap). No calibration debt: this is pure
/// content hashing, not a judge.
/// </summary>
public static class ManifestFingerprint
{
    /// <summary>SHA-256 of <paramref name="canonicalContent"/>, rendered as lowercase hex.</summary>
    public static string Hash(string canonicalContent)
    {
        ArgumentNullException.ThrowIfNull(canonicalContent);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContent));
        // Convert.ToHexStringLower is net9.0+ only; this project multi-targets net8.0, so lower-case explicitly.
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>What changed for one key between a baseline and the current scan.</summary>
public enum ManifestDriftKind
{
    /// <summary>Present in both, same hash — no drift.</summary>
    Unchanged,

    /// <summary>Present in both, DIFFERENT hash — the artifact's content changed since it was trusted (a rug-pull candidate).</summary>
    Changed,

    /// <summary>Present now but not in the baseline — a new artifact, not yet trust-pinned.</summary>
    New,

    /// <summary>Present in the baseline but not now — the artifact disappeared.</summary>
    Removed,
}

/// <summary>One key's drift finding.</summary>
/// <param name="Key">The stable identifier (e.g. a skill name, or a tool name).</param>
/// <param name="Kind">What changed.</param>
/// <param name="BaselineHash">The pinned hash, if any.</param>
/// <param name="CurrentHash">The current hash, if any.</param>
public sealed record ManifestDriftFinding(string Key, ManifestDriftKind Kind, string? BaselineHash, string? CurrentHash);

/// <summary>
/// Compares a baseline set of (key → content-hash) pairs against the current set and reports drift.
/// Pure, deterministic, no I/O — callers own persistence (see
/// <see cref="AgentEval.Skills.SkillManifestBaseline"/> for a JSON-file-backed baseline, mirroring the
/// repo's existing RedTeam baseline/diff CI pattern).
/// </summary>
public static class ManifestDriftDetector
{
    /// <summary>
    /// Detects drift between <paramref name="baselineHashesByKey"/> (the pinned, trusted hashes) and
    /// <paramref name="currentHashesByKey"/> (freshly computed). Every key present in EITHER set is
    /// reported — a removed artifact is as worth surfacing as a changed one.
    /// </summary>
    public static IReadOnlyList<ManifestDriftFinding> Detect(
        IReadOnlyDictionary<string, string> baselineHashesByKey,
        IReadOnlyDictionary<string, string> currentHashesByKey)
    {
        ArgumentNullException.ThrowIfNull(baselineHashesByKey);
        ArgumentNullException.ThrowIfNull(currentHashesByKey);

        var allKeys = new SortedSet<string>(baselineHashesByKey.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(currentHashesByKey.Keys);

        var findings = new List<ManifestDriftFinding>(allKeys.Count);
        foreach (var key in allKeys)
        {
            var hasBaseline = baselineHashesByKey.TryGetValue(key, out var baselineHash);
            var hasCurrent = currentHashesByKey.TryGetValue(key, out var currentHash);

            var kind = (hasBaseline, hasCurrent) switch
            {
                (true, true) when string.Equals(baselineHash, currentHash, StringComparison.Ordinal) => ManifestDriftKind.Unchanged,
                (true, true) => ManifestDriftKind.Changed,
                (false, true) => ManifestDriftKind.New,
                (true, false) => ManifestDriftKind.Removed,
                _ => ManifestDriftKind.Unchanged,   // unreachable (key came from one of the two sets)
            };

            findings.Add(new ManifestDriftFinding(key, kind, baselineHash, currentHash));
        }

        return findings;
    }
}
