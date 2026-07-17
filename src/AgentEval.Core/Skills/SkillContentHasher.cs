// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Guardrails;

namespace AgentEval.Skills;

/// <summary>
/// Agent Skills Wave 1 (§4.1) — the genuinely NEW hash function this wave needed (everything else reuses
/// existing primitives). <see cref="SkillManifestPoisoningGate.Fingerprint"/> only hashes resource/script
/// NAMES (frontmatter-derived), not file bytes, so a resource file's CONTENT silently changing (the text of
/// a <c>.md</c> resource, the body of a script) would not be caught by that fingerprint alone — this fills
/// that gap.
/// </summary>
public static class SkillContentHasher
{
    /// <summary>
    /// Enumerates every file under <paramref name="skillFolderAbsolutePath"/> (SKILL.md + resources/ +
    /// scripts/ + anything else present), sorted by relative path (<see cref="StringComparer.Ordinal"/> —
    /// insensitive to enumeration order, sensitive to any byte changing), SHA-256s each file's bytes, then
    /// reuses <see cref="ManifestFingerprint.Hash"/> over the concatenation of
    /// <c>"{relativePath}\n{fileHashHex}\n"</c> per entry in sorted order — the final hashing step reuses
    /// the existing primitive even though the file-walk itself is new. Bounded, deterministic, no network.
    /// Read-only — never mutates the scanned directory.
    /// </summary>
    public static string HashSkillFolder(string skillFolderAbsolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillFolderAbsolutePath);
        if (!Directory.Exists(skillFolderAbsolutePath))
        {
            throw new DirectoryNotFoundException($"Skill folder not found: {skillFolderAbsolutePath}");
        }

        var relativePaths = Directory.EnumerateFiles(skillFolderAbsolutePath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skillFolderAbsolutePath, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var canonical = new StringBuilder();
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(skillFolderAbsolutePath, relativePath);
            var bytes = File.ReadAllBytes(fullPath);
            var fileHashHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            canonical.Append(relativePath).Append('\n').Append(fileHashHex).Append('\n');
        }

        return ManifestFingerprint.Hash(canonical.ToString());
    }
}
