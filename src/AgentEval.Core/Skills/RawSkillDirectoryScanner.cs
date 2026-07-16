// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// A pure, MAF-free directory walker that finds every candidate <c>SKILL.md</c>-rooted folder under a
/// path, mirroring <b>exactly</b> the candidate set <c>Microsoft.Agents.AI.AgentFileSkillsSource</c> itself
/// would consider — confirmed against the live <c>Microsoft.Agents.AI 1.13.0</c> assembly this session
/// (temporary diagnostic tests, deleted before commit), not assumed from documentation. See
/// <c>strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md</c>.
/// </summary>
/// <remarks>
/// <b>Empirically confirmed discovery convention (not documented anywhere in MAF's public API surface):</b>
/// <list type="number">
/// <item>
/// <b>Single-skill mode is exclusive.</b> If <c>{root}/SKILL.md</c> exists (case-insensitive filename
/// match — confirmed via a <c>SKILL.MD</c> fixture), MAF treats <c>root</c> itself as the ONE candidate
/// skill and does <b>not</b> also scan subdirectories — confirmed by placing a valid subdirectory skill
/// alongside a root-level <c>SKILL.md</c> and observing the subdirectory skill vanish from
/// <c>GetSkillsAsync()</c>'s result entirely, regardless of whether the root-level frontmatter itself was
/// valid. A malformed root-level <c>SKILL.md</c> therefore silently blanks an <em>entire</em> scan, not
/// just itself.
/// </item>
/// <item>
/// <b>Container mode depth is bounded at 2.</b> Absent a root-level <c>SKILL.md</c>, MAF walks
/// subdirectories up to two levels deep (<c>{root}/{a}/SKILL.md</c> and <c>{root}/{a}/{b}/SKILL.md</c>) —
/// confirmed found; a third level (<c>{root}/{a}/{b}/{c}/SKILL.md</c>) was confirmed <b>not</b> found.
/// <c>AgentFileSkillsSourceOptions.SearchDepth</c> defaults to <see langword="null"/> (verified via
/// reflection), so this is MAF's internal default, not a value this reader can read back and must instead
/// mirror as a literal constant.
/// </item>
/// <item>
/// <b>Container-mode candidates are independent.</b> A malformed sibling subdirectory does not affect
/// other subdirectories' discoverability — confirmed with 6 malformed siblings alongside 1 valid one; only
/// the valid one was excluded from the well-formed one's result. (Container mode differs from single-skill
/// mode in this respect — see item 1.)
/// </item>
/// <item>
/// <b>A skill directory's own subtree is never walked for further nested skills</b> — confirmed by placing
/// a second, independently-valid <c>SKILL.md</c> one level inside an already-qualifying skill directory
/// (<c>{root}/skill-x/skill-y/SKILL.md</c>, where <c>skill-x</c> itself has <c>{root}/skill-x/SKILL.md</c>)
/// and observing only <c>skill-x</c> returned. The exclusive single-skill-vs-container rule from item 1
/// applies recursively at every directory level, not just the top-level root the scanner starts from.
/// </item>
/// </list>
/// </remarks>
public static class RawSkillDirectoryScanner
{
    private const int MaxContainerDepth = 2;
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Returns every directory MAF's own discovery would have considered a skill candidate for
    /// <paramref name="rootPath"/> — the raw, unfiltered candidate set, before any GA rule validity check.
    /// Mirrors the exclusive single-skill/container-mode split described in the remarks above.
    /// </summary>
    public static IReadOnlyList<string> FindCandidateSkillDirectories(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        // Single-skill mode: a root-level SKILL.md makes the root itself the only candidate, full stop.
        if (TryFindSkillMdFile(rootPath, out _))
        {
            return new[] { rootPath };
        }

        // Container mode: depth 1 and depth 2 subdirectories only.
        var results = new List<string>();
        CollectContainerCandidates(rootPath, depth: 1, results);
        return results;
    }

    /// <summary>
    /// Locates the <c>SKILL.md</c> file directly inside <paramref name="directory"/>, matching case-
    /// insensitively (confirmed: MAF itself accepts <c>SKILL.MD</c>). Returns <see langword="false"/> if
    /// none exists.
    /// </summary>
    public static bool TryFindSkillMdFile(string directory, out string? skillMdPath)
    {
        if (!Directory.Exists(directory))
        {
            skillMdPath = null;
            return false;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (IOException)
        {
            skillMdPath = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            skillMdPath = null;
            return false;
        }

        // The same exceptions can ALSO surface lazily during enumeration (not just on the EnumerateFiles
        // call itself), so the foreach must be inside the try too — mirrors CollectContainerCandidates'
        // identical sibling guard below, and this session's own "no single permission-restricted folder
        // crashes the whole scan" precedent (see MafSkillScanner's discovery-crash handling).
        try
        {
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), SkillFileName, StringComparison.OrdinalIgnoreCase))
                {
                    skillMdPath = file;
                    return true;
                }
            }
        }
        catch (IOException)
        {
            skillMdPath = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            skillMdPath = null;
            return false;
        }

        skillMdPath = null;
        return false;
    }

    private static void CollectContainerCandidates(string directory, int depth, List<string> results)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (TryFindSkillMdFile(child, out _))
            {
                // Exclusive: once a directory itself qualifies as a skill, its own subtree is never
                // walked for further nested skills (confirmed empirically — see remarks, item 4).
                results.Add(child);
                continue;
            }

            if (depth < MaxContainerDepth)
            {
                CollectContainerCandidates(child, depth + 1, results);
            }
        }
    }
}
