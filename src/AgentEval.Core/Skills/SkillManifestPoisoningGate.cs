// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;

namespace AgentEval.Skills;

/// <summary>
/// MAF Agent Skills Phase 4b: deterministic hash-pin-and-diff drift detection over a skill's manifest —
/// catches a "rug-pull" (a previously-trusted skill's description/instructions/resource-or-script
/// inventory silently changing after it was approved for use). No calibration debt (pure hashing, not a
/// judge); zero new machinery beyond <see cref="ManifestFingerprint"/>/<see cref="ManifestDriftDetector"/>
/// (shared with the future MCP-tool-description equivalent — same pattern, different artifact type).
/// </summary>
public static class SkillManifestPoisoningGate
{
    // Field separator that cannot appear in a skill name/description (U+241E SYMBOL FOR RECORD SEPARATOR) —
    // avoids a canonical-content collision between e.g. Name="a" Description="b|c" and Name="a|b" Description="c".
    private const char FieldSeparator = '␞';

    /// <summary>
    /// Renders the parts of a <see cref="SkillManifest"/> that matter for trust (name, description,
    /// resource/script inventory, allowed-tools, compatibility length) into one canonical string.
    /// Deliberately excludes <see cref="SkillManifest.SourceKind"/>/<see cref="SkillManifest.ParentDirectoryName"/>
    /// (deployment metadata, not model-visible content the agent trusts).
    /// </summary>
    public static string CanonicalContent(SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Join(FieldSeparator,
            manifest.Name,
            manifest.Description ?? string.Empty,
            string.Join(',', manifest.ResourceNames.OrderBy(n => n, StringComparer.Ordinal)),
            string.Join(',', manifest.ScriptNames.OrderBy(n => n, StringComparer.Ordinal)),
            string.Join(',', manifest.AllowedTools.OrderBy(n => n, StringComparer.Ordinal)),
            manifest.CompatibilityLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>SHA-256 fingerprint of <paramref name="manifest"/>'s canonical content.</summary>
    public static string Fingerprint(SkillManifest manifest) => ManifestFingerprint.Hash(CanonicalContent(manifest));

    /// <summary>
    /// Checks the current skill set against a pinned <paramref name="baseline"/> and reports drift —
    /// every skill's fingerprint compared against its trust-time pin, plus new/removed skills.
    /// </summary>
    public static IReadOnlyList<ManifestDriftFinding> CheckDrift(
        IReadOnlyList<SkillManifest> currentSkills, SkillManifestBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(currentSkills);
        ArgumentNullException.ThrowIfNull(baseline);

        // Dictionary KEY only is lowercased — Fingerprint still hashes each manifest's own, original-case
        // Name (CanonicalContent includes it), so this must not touch what's fed to Fingerprint or every
        // fingerprint would desync from a baseline captured on the original casing. GroupBy+First (not
        // ToDictionary directly), the same tolerance SkillsScanCommand.ManifestFingerprintsByName uses: two
        // skills differing only by case is itself the exact rug-pull vector this guards, so it must not
        // crash the check. Without lowercasing here, a skill renamed "myskill" -> "MySkill" (content
        // otherwise identical or poisoned) shows up as an unrelated New+Removed pair instead of a Changed
        // finding, letting a re-cased rug-pull evade drift detection — the exact bypass
        // SkillsScanCommand's own --save-manifest-baseline path already defends against by lowercasing its
        // keys the same way (ManifestDriftDetector.Detect's key union is StringComparer.Ordinal and can't be
        // told to ignore case, so both sides must already match case going in).
        var current = currentSkills
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => Fingerprint(g.First()), StringComparer.Ordinal);
        var baselineHashes = baseline.HashesBySkillName
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.First().Value, StringComparer.Ordinal);

        return ManifestDriftDetector.Detect(baselineHashes, current);
    }
}

/// <summary>
/// A trust-time pin: the fingerprint of every skill at the moment it was reviewed/approved. Persisted as
/// JSON (mirroring the repo's existing RedTeam baseline/diff CI pattern — see
/// <c>AgentEval.RedTeam.Baseline.RedTeamBaseline</c> — same shape: capture → save → later load → compare
/// → fail CI on drift), scoped to skills.
/// </summary>
/// <param name="CapturedAt">When this baseline was captured.</param>
/// <param name="HashesBySkillName">Skill name → <see cref="SkillManifestPoisoningGate.Fingerprint"/> at capture time.</param>
/// <param name="Notes">Optional human note (who approved it, why).</param>
/// <param name="ContentHashesBySkillName">
/// SkillGate (runtime governance) addition — optional, additive. Skill name →
/// <see cref="AgentEval.Skills.SkillContentHasher.HashSkillFolder"/> at capture time, for skills whose
/// on-disk folder was known when this baseline was captured. <see langword="null"/> for any baseline
/// captured before this field existed, or for a skill with no resolvable folder (non-file-sourced) — the
/// structural <see cref="HashesBySkillName"/> fingerprint alone still applies to those. A stronger,
/// OPTIONAL companion signal: two skills can share an identical structural fingerprint (same name/
/// description/resource-and-script inventory) while a resource file's actual BYTES silently changed —
/// this catches that, the structural fingerprint alone cannot.
/// </param>
public sealed record SkillManifestBaseline(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, string> HashesBySkillName,
    string? Notes = null,
    IReadOnlyDictionary<string, string>? ContentHashesBySkillName = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Captures a baseline from the current skill set (fingerprints every skill now).</summary>
    public static SkillManifestBaseline Capture(IReadOnlyList<SkillManifest> skills, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var hashes = skills.ToDictionary(m => m.Name, SkillManifestPoisoningGate.Fingerprint, StringComparer.Ordinal);
        return new SkillManifestBaseline(DateTimeOffset.UtcNow, hashes, notes);
    }

    /// <summary>Serializes this baseline to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes a baseline from JSON (as produced by <see cref="ToJson"/>).</summary>
    public static SkillManifestBaseline FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<SkillManifestBaseline>(json)
            ?? throw new InvalidOperationException("Baseline JSON deserialized to null.");
    }

    /// <summary>Saves this baseline to a file (CI recipe: commit this file; a later run/CI job loads and compares against it).</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        => await File.WriteAllTextAsync(path, ToJson(), cancellationToken).ConfigureAwait(false);

    /// <summary>Loads a baseline from a file.</summary>
    public static async Task<SkillManifestBaseline> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return FromJson(json);
    }
}
