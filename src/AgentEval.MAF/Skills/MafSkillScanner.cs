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
    public static async Task<SkillComplianceReport> ScanFileSkillsAsync(
        string skillPath, AIAgent agent, SkillScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillPath))
        {
            throw new ArgumentException("skillPath must be non-empty.", nameof(skillPath));
        }

        ArgumentNullException.ThrowIfNull(agent);

        using var source = new AgentFileSkillsSource(skillPath);
        var context = new AgentSkillsSourceContext(agent, session: null);
        return await ScanAsync(source, context, options, cancellationToken).ConfigureAwait(false);
    }

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
