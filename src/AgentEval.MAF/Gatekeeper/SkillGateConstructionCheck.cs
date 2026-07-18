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

        // Serializes the whole read-check-persist sequence, not just the final write: two agents under
        // SkillGateMode.Bootstrap constructing concurrently (e.g. a host spinning up several agent instances
        // at startup) would otherwise both LoadOrEmpty the SAME pre-bootstrap baseline, each compute its own
        // "newly seen" set, and whichever PersistBootstrappedBaseline call lands last silently overwrites the
        // other's additions (a lost-update race) — one agent's newly-trusted skill never actually gets
        // pinned. Construction-time-only (per this class's own remarks), so a process-wide lock costs nothing
        // on any hot path.
        lock (s_bootstrapLock)
        {
            CheckAndEnforceCore(skills, baselinePath, mode);
        }
    }

    private static readonly object s_bootstrapLock = new();

    private static void CheckAndEnforceCore(IReadOnlyList<ScannedSkillInfo> skills, string baselinePath, SkillGateMode mode)
    {
        var baseline = LoadOrEmpty(baselinePath);
        var manifests = skills.Select(s => s.Manifest).ToList();
        var structural = SkillManifestPoisoningGate.CheckDrift(manifests, baseline);

        var changed = structural.Where(f => f.Kind == ManifestDriftKind.Changed).ToList();
        var unknown = structural.Where(f => f.Kind == ManifestDriftKind.New).ToList();

        var contentBaseline = baseline.ContentHashesBySkillName ?? EmptyHashes;
        var contentCurrent = HashCurrentContent(skills, contentBaseline);
        var contentChanged = contentCurrent.Count == 0
            ? Array.Empty<ManifestDriftFinding>()
            : ManifestDriftDetector.Detect(contentBaseline, contentCurrent)
                .Where(f => f.Kind == ManifestDriftKind.Changed)
                .ToArray();

        var blocking = new List<ManifestDriftFinding>(changed);
        // A content-only change on a skill the structural pass ALREADY flagged is not reported twice —
        // the structural finding already names the skill and blocks; a second, differently-worded finding
        // for the same key would be redundant, not additional information. OrdinalIgnoreCase: `changed[].Key`
        // is lowercased (SkillManifestPoisoningGate.CheckDrift), `cf.Key` (content-hash side) is the skill's
        // original-case name — an Ordinal compare would miss the match for a non-lowercase name and let the
        // same skill appear twice in `blocking`.
        blocking.AddRange(contentChanged.Where(cf => changed.All(f => !string.Equals(f.Key, cf.Key, StringComparison.OrdinalIgnoreCase))));

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

    /// <summary>
    /// Hashes the on-disk folder for every skill the baseline captured a content hash for. A skill whose
    /// folder has since moved/vanished, or whose files can't be read right now (lock/permission race), is
    /// skipped rather than thrown — matches <c>SkillsScanCommand.ToBaselineEntry</c>'s established guard:
    /// this runs during agent CONSTRUCTION, and an IO hiccup unrelated to actual skill drift must not crash
    /// startup. That skill's structural fingerprint check (always computed above) still fully applies; only
    /// the stronger content-hash comparison is skipped for it this round.
    /// </summary>
    private static Dictionary<string, string> HashCurrentContent(
        IReadOnlyList<ScannedSkillInfo> skills, IReadOnlyDictionary<string, string> contentBaseline)
    {
        var contentCurrent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            if (skill.AbsolutePath is null || !contentBaseline.ContainsKey(skill.Manifest.Name)
                || !Directory.Exists(skill.AbsolutePath))
            {
                continue;
            }

            try
            {
                contentCurrent[skill.Manifest.Name] = SkillContentHasher.HashSkillFolder(skill.AbsolutePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return contentCurrent;
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
        // OrdinalIgnoreCase: unknown[].Key now comes from SkillManifestPoisoningGate.CheckDrift, which
        // lowercases its own dictionary keys (see that method's remarks) — skill.Manifest.Name below is the
        // ORIGINAL casing. Ordinal would silently fail this Contains() check for any skill whose name isn't
        // already all-lowercase, skipping its content-hash bootstrap for no reason related to the check itself.
        var unknownNames = new HashSet<string>(unknown.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            if (!unknownNames.Contains(skill.Manifest.Name) || skill.AbsolutePath is null
                || !Directory.Exists(skill.AbsolutePath))
            {
                continue;
            }

            try
            {
                extendedContentHashes[skill.Manifest.Name] = SkillContentHasher.HashSkillFolder(skill.AbsolutePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same guard as HashCurrentContent above: bootstrap-persisting a NEW skill's content hash must
                // not crash construction over an IO hiccup — the skill still gets a structural fingerprint
                // baseline entry (extendedHashes, above); only its content hash is skipped this round.
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
