// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Skills;

/// <summary>
/// The ONLY place in this feature that touches a live <c>AgentSkill</c> / <c>AgentSkillsSource</c> — maps
/// GA MAF Agent Skills types to the pure, MAF-free <see cref="SkillManifest"/> DTO, then delegates to
/// <see cref="SkillComplianceValidator"/> (which never sees a <c>Microsoft.Agents.AI</c> type; see the design
/// doc's "the build enforces the boundary" principle: <c>AgentEval.Core</c> does not reference
/// <c>Microsoft.Agents.AI</c>, so a leak would fail to compile).
/// </summary>
/// <remarks>
/// <b>Resource/script enumeration is a documented, honest approximation — not a public MAF API.</b>
/// Verified this session (reflection over the live <c>Microsoft.Agents.AI 1.13.0</c> assembly):
/// <c>AgentFileSkill</c> stores its discovered resources/scripts in <b>private</b> fields
/// (<c>_resources</c> / <c>_scripts</c>) with no public getter, and <c>AgentSkill</c>/<c>AgentSkillFrontmatter</c>
/// expose no list either — only <c>GetResourceAsync(name)</c> / <c>GetScriptAsync(name)</c>, an exact-match
/// lookup against MAF's own internal (unexposed) list. So this scanner independently re-derives the resource
/// and script inventory for a <b>file-sourced</b> skill by walking its <c>resources/</c> and <c>scripts/</c>
/// subdirectories on disk — the same directory convention MAF's own <c>AgentFileSkillsSourceOptions</c>
/// (<c>AllowedResourceExtensions</c> / <c>AllowedScriptExtensions</c> / <c>SearchDepth</c>) discovers by, and
/// the same relative-path naming already used by the shipped Phase 1 sample fixture (e.g.
/// <c>"resources/policy.md"</c>). For non-file sources (in-memory/class/MCP/custom) there is no equivalent
/// way to enumerate names without a MAF API, so <see cref="SkillManifest.ResourceNames"/>/
/// <see cref="SkillManifest.ScriptNames"/> are honestly reported empty for those — never guessed or
/// fabricated. This is a real, acknowledged limitation, not hidden.
/// <para>
/// <b>Consequence:</b> the governance flags that key off resource/script PRESENCE
/// (<see cref="SkillComplianceRule.ScriptRequiresGovernanceReview"/>,
/// <see cref="SkillComplianceRule.ResourceFromUntrustedSource"/>) can only fire for file-sourced skills —
/// an in-memory/class/MCP skill with an actual script (e.g. built via <c>AgentInlineSkill.AddScript</c>)
/// will NOT be flagged, because this scanner cannot see it either. A production deployment that registers
/// non-file skills with scripts should not rely on this scanner alone to surface that fact.
/// </para>
/// </remarks>
public static class MafSkillScanner
{
    private static readonly string[] ResourceExtensions = { ".md", ".txt", ".json", ".csv", ".yaml", ".yml" };
    private static readonly string[] ScriptExtensions = { ".csx", ".cs", ".py", ".js", ".ts", ".sh", ".ps1" };

    /// <summary>
    /// Enumerates skills from a live <paramref name="source"/> (source-level <c>GetSkillsAsync</c> — GA
    /// exposes no provider-level convenience, verified this session), maps each to a <see cref="SkillManifest"/>,
    /// then delegates to <see cref="SkillComplianceValidator.Validate"/>.
    /// </summary>
    public static async Task<SkillComplianceReport> ScanAsync(
        AgentSkillsSource source,
        AgentSkillsSourceContext context,
        SkillScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var skills = await source.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
        var manifests = new List<SkillManifest>(skills.Count);
        foreach (var skill in skills)
        {
            manifests.Add(ToManifest(skill));
        }

        return SkillComplianceValidator.Validate(manifests, options);
    }

    /// <summary>
    /// Convenience: builds an <see cref="AgentFileSkillsSource"/> over <paramref name="skillPath"/> and scans
    /// it, deriving the <see cref="AgentSkillsSourceContext"/> from <paramref name="agent"/>.
    /// </summary>
    /// <remarks>
    /// <b>Silent-discovery-exclusion detection (Item 5):</b> unlike <see cref="ScanAsync"/> (unchanged —
    /// it only ever sees what a live <see cref="AgentSkillsSource"/> hands back), this method additionally
    /// runs a second, independent raw directory walk (<see cref="RawSkillDirectoryScanner"/>, mirroring
    /// MAF's own confirmed discovery convention) over <paramref name="skillPath"/> and reconciles it against
    /// what <see cref="AgentFileSkillsSource.GetSkillsAsync"/> actually returned. Any folder present on disk
    /// but absent from MAF's returned set is a skill MAF silently excluded — see
    /// <c>strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md</c>. Each becomes a
    /// <see cref="SkillComplianceRule.SkillExcludedFromDiscovery"/> High finding whose message is built by
    /// re-running the raw-parsed frontmatter through the SAME rule set <see cref="SkillComplianceValidator"/>
    /// applies to every normally-discovered skill (<see cref="SkillComplianceValidator.ValidateSingle"/>) —
    /// one rule set, two callers, never duplicated.
    /// <para>
    /// <b>Bonus defensive fix, found during this same verification (not originally scoped):</b> a SKILL.md
    /// violating certain GA hard limits (confirmed: <c>compatibility</c> &gt; 500 characters) does not
    /// silently exclude — it makes <c>GetSkillsAsync()</c> <em>throw</em>, which without this try/catch
    /// would previously crash the entire scan (and the <c>agenteval skills scan</c> CLI verb) for every
    /// skill under <paramref name="skillPath"/>, not just the offending one. This is now caught and reported
    /// as a single, clearly-labeled High finding instead of an unhandled stack trace.
    /// </para>
    /// </remarks>
    public static async Task<SkillComplianceReport> ScanFileSkillsAsync(
        string skillPath, AIAgent agent, SkillScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillPath))
        {
            throw new ArgumentException("skillPath must be non-empty.", nameof(skillPath));
        }

        ArgumentNullException.ThrowIfNull(agent);
        options ??= new SkillScanOptions();

        using var source = new AgentFileSkillsSource(skillPath);
        var context = new AgentSkillsSourceContext(agent, session: null);

        IList<AgentSkill> skills;
        try
        {
            skills = await source.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Discovery crashed entirely for this source — a MAF-enforced hard limit (not a silent
            // exclusion) somewhere under skillPath. See remarks above.
            var crashFinding = new SkillComplianceFinding(
                "(discovery failure)",
                SkillComplianceRule.SkillExcludedFromDiscovery,
                Severity.High,
                $"Skill discovery under '{skillPath}' failed entirely — MAF threw {ex.GetType().Name}: '{ex.Message}'. " +
                "This usually means one SKILL.md under this path violates a MAF-enforced hard limit (e.g. " +
                "'compatibility' over 500 characters) that MAF treats as fatal rather than silently excluding. " +
                "No skills could be validated until the offending file is fixed. This is not a warning; " +
                "nothing under this path is functional until discovery succeeds.",
                null);
            var emptyHistogram = new Dictionary<string, int> { ["load"] = 0, ["read"] = 0, ["run"] = 0 };
            return new SkillComplianceReport(
                new[] { crashFinding },
                new SkillCoverageSummary(0, 0, 0, emptyHistogram, SilentlyExcludedCount: 0));
        }

        var manifests = new List<SkillManifest>(skills.Count);
        var returnedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            manifests.Add(ToManifest(skill));
            if (skill is AgentFileSkill fileSkill && !string.IsNullOrWhiteSpace(fileSkill.Path))
            {
                returnedDirectories.Add(NormalizeDirectory(fileSkill.Path));
            }
        }

        var baseReport = SkillComplianceValidator.Validate(manifests, options);

        var exclusionFindings = FindSilentExclusions(skillPath, returnedDirectories, options);
        if (exclusionFindings.Count == 0)
        {
            return baseReport;
        }

        var combinedFindings = new List<SkillComplianceFinding>(baseReport.Findings.Count + exclusionFindings.Count);
        combinedFindings.AddRange(baseReport.Findings);
        combinedFindings.AddRange(exclusionFindings);
        var combinedCoverage = baseReport.Coverage with { SilentlyExcludedCount = exclusionFindings.Count };
        return new SkillComplianceReport(combinedFindings, combinedCoverage);
    }

    /// <summary>
    /// Pass 2 + reconciliation: walks <paramref name="skillPath"/> the same way MAF's own discovery would
    /// (<see cref="RawSkillDirectoryScanner"/>), and for every candidate directory <em>not</em> present in
    /// <paramref name="returnedDirectories"/> (pass 1's actual result), builds one
    /// <see cref="SkillComplianceRule.SkillExcludedFromDiscovery"/> finding explaining why.
    /// </summary>
    private static List<SkillComplianceFinding> FindSilentExclusions(
        string skillPath, HashSet<string> returnedDirectories, SkillScanOptions options)
    {
        var findings = new List<SkillComplianceFinding>();
        var candidates = RawSkillDirectoryScanner.FindCandidateSkillDirectories(skillPath);

        foreach (var dir in candidates)
        {
            if (returnedDirectories.Contains(NormalizeDirectory(dir)))
            {
                continue; // MAF returned it — not a silent exclusion.
            }

            RawSkillDirectoryScanner.TryFindSkillMdFile(dir, out var skillMdPath);
            var raw = RawSkillFrontmatterReader.Read(skillMdPath);
            var (resourceNames, scriptNames) = DiscoverFileSkillAssets(dir);
            var rawManifest = new SkillManifest(
                Name: raw.Name ?? string.Empty,
                Description: raw.Description,
                ResourceNames: resourceNames,
                ScriptNames: scriptNames,
                CompatibilityLength: raw.Compatibility?.Length,
                AllowedTools: Array.Empty<string>(),
                SourceKind: SkillSourceKind.File,
                ParentDirectoryName: SafeDirectoryName(dir));

            var label = string.IsNullOrWhiteSpace(rawManifest.Name) ? "(unnamed skill)" : rawManifest.Name;
            var granular = SkillComplianceValidator.ValidateSingle(rawManifest, options);
            // Candidate reasons: every GA-authoring-rule finding (name/description/compatibility), at ANY
            // severity — NOT just High. DescriptionTooLong is Medium but is a confirmed real MAF silent-
            // exclusion trigger (see RawSkillDirectoryScanner's remarks), so filtering by severity alone
            // would wrongly fall through to the generic "might be malformed YAML" fallback for it. Excludes
            // the three AgentEval-OWNED governance/experimental flags (ScriptRequiresGovernanceReview /
            // ResourceFromUntrustedSource / AllowedToolsExperimental) explicitly, not by severity threshold
            // — those are opinions about scripts/resources/allowed-tools, never something MAF's own parser
            // could plausibly reject a file for, so including them here would be a NEW kind of misleading
            // reason rather than a fix.
            var violatedRuleReasons = granular
                .Where(f => f.Rule is not (SkillComplianceRule.ScriptRequiresGovernanceReview
                    or SkillComplianceRule.ResourceFromUntrustedSource
                    or SkillComplianceRule.AllowedToolsExperimental))
                .Select(f => f.Message)
                .ToList();

            var reasonText = violatedRuleReasons.Count > 0
                ? string.Join(" ", violatedRuleReasons)
                : "AgentEval's own rule-checker found no GA violation after reparsing this SKILL.md " +
                  $"(name='{rawManifest.Name}', description-present={!string.IsNullOrWhiteSpace(rawManifest.Description)}) " +
                  "— this may indicate a MAF-side parsing difference (e.g. malformed YAML); inspect the file directly.";

            findings.Add(new SkillComplianceFinding(
                label,
                SkillComplianceRule.SkillExcludedFromDiscovery,
                Severity.High,
                $"Skill folder '{dir}' will NEVER be loaded — MAF excludes it silently because: {reasonText} " +
                "This is not a warning; the skill is non-functional as authored.",
                null));
        }

        return findings;
    }

    // Canonicalizes to an absolute, fully-qualified form before comparing. This matters because the two
    // sides being reconciled do NOT start out in the same shape: MAF's own AgentFileSkill.Path is always
    // absolute, but RawSkillDirectoryScanner's candidates are derived from whatever form the CALLER's
    // skillPath argument had — if a caller passes a relative path (e.g. "agenteval skills scan ./skills"
    // from the CLI), every raw-walk candidate stayed relative while returnedDirectories was all-absolute, so
    // a plain TrimEnd-only comparison never matched ANYTHING and every skill was falsely flagged excluded.
    // Path.GetFullPath resolves relative segments against the current directory (the same base any relative
    // path passed to AgentFileSkillsSource's constructor would resolve against) and normalizes separators.
    // Case-insensitive on Windows/macOS (matches the observed filesystem); on case-sensitive Linux
    // filesystems this can only make MORE candidates match pass 1's returned set (never fewer), so it
    // errs toward NOT reporting a false exclusion rather than toward missing a real one.
    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Maps a live <see cref="AgentSkill"/> to the pure <see cref="SkillManifest"/> DTO. Public and testable
    /// without a live source (construct any <see cref="AgentSkill"/> — e.g. via
    /// <c>AgentInMemorySkillsSource</c>/<c>AgentInlineSkill</c> — and call this directly).
    /// </summary>
    /// <param name="skill">The live skill.</param>
    /// <param name="sourceKindOverride">
    /// Override the detected <see cref="SkillSourceKind"/>. When omitted, inferred from the CLR type
    /// (<see cref="AgentFileSkill"/> → <see cref="SkillSourceKind.File"/>; anything else → <see cref="SkillSourceKind.InMemory"/>,
    /// the honest default absent stronger type information — callers with MCP/Class/Custom sources should
    /// pass the correct kind explicitly so <see cref="SkillComplianceRule.ResourceFromUntrustedSource"/> fires correctly).
    /// </param>
    public static SkillManifest ToManifest(AgentSkill skill, SkillSourceKind? sourceKindOverride = null)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var frontmatter = skill.Frontmatter;
        var allowedTools = string.IsNullOrWhiteSpace(frontmatter?.AllowedTools)
            ? Array.Empty<string>()
            : frontmatter.AllowedTools.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (skill is AgentFileSkill fileSkill)
        {
            var kind = sourceKindOverride ?? SkillSourceKind.File;
            var (resourceNames, scriptNames) = DiscoverFileSkillAssets(fileSkill.Path);
            var parentDir = SafeDirectoryName(fileSkill.Path);
            return new SkillManifest(
                Name: frontmatter?.Name ?? string.Empty,
                Description: frontmatter?.Description,
                ResourceNames: resourceNames,
                ScriptNames: scriptNames,
                CompatibilityLength: frontmatter?.Compatibility?.Length,
                AllowedTools: allowedTools,
                SourceKind: kind,
                ParentDirectoryName: parentDir);
        }

        // Non-file source: honestly report no discoverable resources/scripts rather than guessing (see remarks).
        return new SkillManifest(
            Name: frontmatter?.Name ?? string.Empty,
            Description: frontmatter?.Description,
            ResourceNames: Array.Empty<string>(),
            ScriptNames: Array.Empty<string>(),
            CompatibilityLength: frontmatter?.Compatibility?.Length,
            AllowedTools: allowedTools,
            SourceKind: sourceKindOverride ?? SkillSourceKind.InMemory,
            ParentDirectoryName: null);
    }

    private static (IReadOnlyList<string> Resources, IReadOnlyList<string> Scripts) DiscoverFileSkillAssets(string? skillDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillDirectory) || !Directory.Exists(skillDirectory))
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var resources = EnumerateRelative(skillDirectory, "resources", ResourceExtensions);
        var scripts = EnumerateRelative(skillDirectory, "scripts", ScriptExtensions);
        return (resources, scripts);
    }

    private static IReadOnlyList<string> EnumerateRelative(string skillDirectory, string subfolder, string[] extensions)
    {
        var dir = Path.Combine(skillDirectory, subfolder);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (extensions.Length > 0 && !extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(skillDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            results.Add(relative);
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string? SafeDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // AgentFileSkill.Path may be the skill's own directory (containing SKILL.md) — the name-matches-dir
        // GA rule compares the skill's declared name against THIS directory's name.
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) ? null : Path.GetFileName(trimmed);
    }
}
