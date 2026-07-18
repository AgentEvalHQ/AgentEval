// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Skills;
using AgentEval.Skills;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// SkillGate Tier 1 — Agent Skills governance moves from audit-time-only (<c>agenteval skills scan</c>) to
/// enforcement-time: refuses to let an agent construct at all if a skill it will expose has drifted from its
/// pinned baseline, or (under <see cref="SkillGateMode.Strict"/>) is not recognized at all.
/// </summary>
/// <remarks>
/// <b>Construction-time-only, not a runtime seam — deliberately.</b> This is the THIRD application of the
/// same pattern already used for skill manifests (<see cref="SkillManifestPoisoningGate"/>, audit-time) and
/// prompt templates (<see cref="PromptTemplateDriftGate"/>/<see cref="PromptTemplateDriftException"/>,
/// enforcement-time) — a skill's configured set doesn't change mid-run (an <c>AgentSkillsSource</c> is wired
/// once at agent construction), so a per-turn check would be pure waste. A long-running server whose skill
/// FOLDER is modified on disk WHILE already constructed is a narrower, real threat this check cannot catch —
/// that is the opt-in, per-call Tier 2 gate (a future increment, not built here).
/// <para>
/// <b>Two independent hash signals, both optional per skill.</b> The STRUCTURAL fingerprint
/// (<see cref="SkillManifestPoisoningGate.Fingerprint"/> — name/description/resource-and-script inventory/
/// allowed-tools/compatibility-length) always applies. The stronger CONTENT hash
/// (<see cref="SkillContentHasher.HashSkillFolder"/> — every file's actual bytes) additionally applies only
/// when BOTH the baseline captured a content hash for that skill name AND the current scan resolved that
/// skill's on-disk folder (file-sourced skills only). A content-only change (a resource file's body edited,
/// nothing in the frontmatter) would be invisible to the structural fingerprint alone — this is exactly the
/// gap Wave 1's <see cref="SkillContentHasher"/> was built to close, reused here at enforcement time instead
/// of only at audit time.
/// </para>
/// </remarks>
public static class SkillGateConstructionCheck
{
    /// <summary>
    /// Loads the baseline at <paramref name="baselinePath"/> (or starts from an empty one if the file does
    /// not exist yet — see <see cref="SkillGateMode"/> for why an empty baseline under
    /// <see cref="SkillGateMode.Strict"/> refuses EVERY skill, and how to bootstrap past that on a first-ever
    /// run), computes drift against <paramref name="skills"/>, and either throws <see cref="SkillDriftException"/>
    /// or — under <see cref="SkillGateMode.Bootstrap"/> with newly-seen skills — persists an extended baseline
    /// back to <paramref name="baselinePath"/>.
    /// </summary>
    public static void CheckAndEnforce(IReadOnlyList<ScannedSkillInfo> skills, string baselinePath, SkillGateMode mode)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);

        var baseline = LoadOrEmpty(baselinePath);
        var manifests = skills.Select(s => s.Manifest).ToList();
        var structural = SkillManifestPoisoningGate.CheckDrift(manifests, baseline);

        var changed = structural.Where(f => f.Kind == ManifestDriftKind.Changed).ToList();
        var unknown = structural.Where(f => f.Kind == ManifestDriftKind.New).ToList();

        var contentBaseline = baseline.ContentHashesBySkillName ?? EmptyHashes;
        var contentCurrent = skills
            .Where(s => s.AbsolutePath is not null && contentBaseline.ContainsKey(s.Manifest.Name))
            .ToDictionary(s => s.Manifest.Name, s => SkillContentHasher.HashSkillFolder(s.AbsolutePath!), StringComparer.Ordinal);
        var contentChanged = contentCurrent.Count == 0
            ? Array.Empty<ManifestDriftFinding>()
            : ManifestDriftDetector.Detect(contentBaseline, contentCurrent)
                .Where(f => f.Kind == ManifestDriftKind.Changed)
                .ToArray();

        var blocking = new List<ManifestDriftFinding>(changed);
        // A content-only change on a skill the structural pass ALREADY flagged is not reported twice —
        // the structural finding already names the skill and blocks; a second, differently-worded finding
        // for the same key would be redundant, not additional information.
        blocking.AddRange(contentChanged.Where(cf => changed.All(f => f.Key != cf.Key)));

        if (mode == SkillGateMode.Strict)
        {
            blocking.AddRange(unknown);
        }

        if (blocking.Count > 0)
        {
            throw new SkillDriftException(blocking);
        }

        if (mode == SkillGateMode.Bootstrap && unknown.Count > 0)
        {
            PersistBootstrappedBaseline(baseline, baselinePath, skills, unknown, contentBaseline);
        }
    }

    private static SkillManifestBaseline LoadOrEmpty(string baselinePath)
    {
        if (!File.Exists(baselinePath))
        {
            return new SkillManifestBaseline(DateTimeOffset.UtcNow, EmptyHashes, ContentHashesBySkillName: EmptyHashes);
        }

        // Sync I/O is deliberate: UseGatekeeper's whole configure/registration path is synchronous (see
        // AgentEvalGatekeeperExtensions — the PromptTemplateDrift check reads its inputs from memory only, but
        // this check, unlike that one, needs to read a FILE, and UseGatekeeper has no async seam to do that
        // through), matching the same File.ReadAllText/WriteAllText-not-Async choice already used elsewhere in
        // this builder's synchronous construction path.
        var json = File.ReadAllText(baselinePath);
        return SkillManifestBaseline.FromJson(json);
    }

    private static void PersistBootstrappedBaseline(
        SkillManifestBaseline baseline,
        string baselinePath,
        IReadOnlyList<ScannedSkillInfo> skills,
        IReadOnlyList<ManifestDriftFinding> unknown,
        IReadOnlyDictionary<string, string> contentBaseline)
    {
        var extendedHashes = new Dictionary<string, string>(baseline.HashesBySkillName, StringComparer.Ordinal);
        foreach (var finding in unknown)
        {
            // CurrentHash is always non-null for a ManifestDriftKind.New finding (Detect only emits New when
            // the key IS present on the current side) — see ManifestDriftDetector.Detect's own (hasBaseline,
            // hasCurrent) switch.
            extendedHashes[finding.Key] = finding.CurrentHash!;
        }

        var extendedContentHashes = new Dictionary<string, string>(contentBaseline, StringComparer.Ordinal);
        var unknownNames = new HashSet<string>(unknown.Select(f => f.Key), StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            if (unknownNames.Contains(skill.Manifest.Name) && skill.AbsolutePath is not null)
            {
                extendedContentHashes[skill.Manifest.Name] = SkillContentHasher.HashSkillFolder(skill.AbsolutePath);
            }
        }

        var updated = baseline with
        {
            CapturedAt = DateTimeOffset.UtcNow,
            HashesBySkillName = extendedHashes,
            ContentHashesBySkillName = extendedContentHashes.Count > 0 ? extendedContentHashes : baseline.ContentHashesBySkillName,
        };

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(baselinePath));
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        File.WriteAllText(baselinePath, updated.ToJson());
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHashes = new Dictionary<string, string>();
}
