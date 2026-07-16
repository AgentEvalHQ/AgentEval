// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// The raw <c>name</c>/<c>description</c>/<c>compatibility</c> values recovered directly from a
/// <c>SKILL.md</c> file's frontmatter block, independent of whether MAF's own
/// <c>AgentFileSkillsSource</c> would accept or silently exclude it.
/// </summary>
public sealed record RawSkillFrontmatter(string? Name, string? Description, string? Compatibility);

/// <summary>
/// A deliberately minimal, MAF-free <c>SKILL.md</c> frontmatter reader — extracts <c>name</c>/
/// <c>description</c>/<c>compatibility</c> from the leading <c>---</c>-delimited block via a simple
/// line scan, not a general YAML parser.
/// </summary>
/// <remarks>
/// <b>Why this exists (honest deviation from the original design sketch):</b> the design doc
/// (<c>strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md</c> §3) assumed
/// reusable frontmatter-parsing logic already existed somewhere in this codebase for
/// <see cref="SkillManifest"/>/<see cref="SkillComplianceValidator"/> to call directly. It does not —
/// grepped for it before writing this file: every existing frontmatter read in this repo goes through
/// MAF's own (private, unexported) <c>AgentFileSkillsSource</c> parser via <c>AgentSkill.Frontmatter</c>.
/// A silently-excluded skill is, by definition, one MAF's own parser refused to hand back — so reaching
/// it at all requires an independent reader. This one is intentionally narrow: it only needs to recover
/// enough to run <see cref="SkillComplianceValidator.ValidateSingle"/> and explain <em>why</em> MAF
/// excluded the folder, not to replace or reimplement MAF's parser.
/// </remarks>
public static class RawSkillFrontmatterReader
{
    /// <summary>
    /// Reads <paramref name="skillMdPath"/> and extracts the frontmatter fields this scanner cares about.
    /// Never throws on malformed content — a missing file, missing frontmatter delimiters, or missing keys
    /// simply yield <see langword="null"/> fields (which the reused GA rule set already treats as
    /// <c>NameMissing</c>/<c>DescriptionMissing</c>), consistent with this feature's "explain, don't crash"
    /// discipline.
    /// </summary>
    public static RawSkillFrontmatter Read(string? skillMdPath)
    {
        if (string.IsNullOrWhiteSpace(skillMdPath) || !File.Exists(skillMdPath))
        {
            return new RawSkillFrontmatter(null, null, null);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(skillMdPath);
        }
        catch (IOException)
        {
            return new RawSkillFrontmatter(null, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new RawSkillFrontmatter(null, null, null);
        }

        return Parse(lines);
    }

    /// <summary>Parses already-read lines — split out for direct unit testing without touching disk.</summary>
    public static RawSkillFrontmatter Parse(IReadOnlyList<string> lines)
    {
        // Frontmatter must open with a line that is exactly "---" (optionally trailing whitespace).
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue; // tolerate leading blank lines before the opening delimiter
            }

            start = trimmed == "---" ? i : -1;
            break;
        }

        if (start < 0)
        {
            return new RawSkillFrontmatter(null, null, null);
        }

        string? name = null;
        string? description = null;
        string? compatibility = null;

        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break; // closing delimiter — stop scanning
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            // Strip a single layer of matching quotes, the common YAML-authoring convention.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "compatibility":
                    compatibility = value;
                    break;
            }
        }

        return new RawSkillFrontmatter(
            string.IsNullOrEmpty(name) ? null : name,
            string.IsNullOrEmpty(description) ? null : description,
            string.IsNullOrEmpty(compatibility) ? null : compatibility);
    }
}
