// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.Skills;

/// <summary>
/// Pure, deterministic, MAF-free validator for <see cref="SkillManifest"/>s against the GA <c>SKILL.md</c>
/// rules plus AgentEval's own governance/trust-boundary flags. No I/O, no LLM, no <c>Microsoft.Agents.AI</c>
/// reference — fully unit-testable without a live MAF assembly. See
/// <c>strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md</c> §5.
/// </summary>
public static class SkillComplianceValidator
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;
    private const int MaxCompatibilityLength = 500;

    // name must be lowercase letters, digits, and single hyphens (GA rule). Bounded (no backtracking risk —
    // simple character class), so no ReDoS budget needed.
    private static readonly Regex ValidNameChars = new("^[a-z0-9-]*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <summary>Validates every skill in <paramref name="skills"/> and returns the aggregate report.</summary>
    public static SkillComplianceReport Validate(IReadOnlyList<SkillManifest> skills, SkillScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        options ??= new SkillScanOptions();

        var findings = new List<SkillComplianceFinding>();
        var withResources = 0;
        var withScripts = 0;
        var histogram = new Dictionary<string, int> { ["load"] = 0, ["read"] = 0, ["run"] = 0 };

        foreach (var skill in skills)
        {
            ValidateOne(skill, options, findings);

            // "load" is always reachable — every enumerated skill can be load_skill'd.
            histogram["load"]++;
            if (skill.HasResources)
            {
                withResources++;
                histogram["read"]++;
            }

            if (skill.HasScripts)
            {
                withScripts++;
                histogram["run"]++;
            }
        }

        var coverage = new SkillCoverageSummary(skills.Count, withResources, withScripts, histogram);
        return new SkillComplianceReport(findings, coverage);
    }

    /// <summary>
    /// Runs the full GA rule set (name/description/compatibility) plus AgentEval's governance flags
    /// against a single <see cref="SkillManifest"/>, without the list-aggregation <see cref="Validate"/>
    /// otherwise does. This is the SAME rule-checking logic <see cref="ValidateOne"/> has always applied
    /// per-skill inside <see cref="Validate"/> — extracted as a public entry point so a second, independent
    /// caller (the silent-discovery-exclusion reconciliation in <c>AgentEval.MAF.Skills.MafSkillScanner</c>)
    /// can run a raw-parsed, MAF-never-returned manifest through the identical rules rather than
    /// duplicating them. One rule set, two callers — see
    /// <c>strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md</c> §2.3.
    /// </summary>
    public static IReadOnlyList<SkillComplianceFinding> ValidateSingle(SkillManifest skill, SkillScanOptions? options = null)
    {
        options ??= new SkillScanOptions();
        var findings = new List<SkillComplianceFinding>();
        ValidateOne(skill, options, findings);
        return findings;
    }

    private static void ValidateOne(SkillManifest skill, SkillScanOptions options, List<SkillComplianceFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var name = skill.Name;
        // A missing name can't be attributed to anything else, so use a stand-in label for the finding's
        // SkillName field rather than a null/empty key that would collide across multiple malformed skills.
        var label = string.IsNullOrWhiteSpace(name) ? "(unnamed skill)" : name;

        // ── name rules ──
        if (string.IsNullOrWhiteSpace(name))
        {
            Add(findings, label, SkillComplianceRule.NameMissing, Severity.High,
                "Skill 'name' is missing or empty — GA requires a non-empty name.", "name");
        }
        else
        {
            if (name.Length > MaxNameLength)
            {
                Add(findings, label, SkillComplianceRule.NameTooLong, Severity.High,
                    $"Skill name '{name}' is {name.Length} characters — GA limits 'name' to {MaxNameLength}.", "name");
            }

            if (!ValidNameChars.IsMatch(name))
            {
                Add(findings, label, SkillComplianceRule.NameInvalidChars, Severity.High,
                    $"Skill name '{name}' contains characters outside [a-z0-9-] — GA requires lowercase letters, digits, and hyphens only.", "name");
            }

            if (name.Contains("--", StringComparison.Ordinal))
            {
                Add(findings, label, SkillComplianceRule.NameConsecutiveHyphens, Severity.High,
                    $"Skill name '{name}' contains consecutive hyphens — GA disallows '--'.", "name");
            }

            if (options.EnforceNameMatchesDirectory
                && skill.SourceKind == SkillSourceKind.File
                && skill.ParentDirectoryName is { Length: > 0 } dir
                && !string.Equals(name, dir, StringComparison.Ordinal))
            {
                Add(findings, label, SkillComplianceRule.NameMismatchesDirectory, Severity.High,
                    $"Skill name '{name}' does not match its parent directory '{dir}' — GA requires an exact match for file-sourced skills.", "name");
            }
        }

        // ── description rules ──
        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            Add(findings, label, SkillComplianceRule.DescriptionMissing, Severity.High,
                "Skill 'description' is missing or empty — GA requires a non-empty description (it is what the model sees to decide when to load the skill).", "description");
        }
        else if (skill.Description.Length > MaxDescriptionLength)
        {
            Add(findings, label, SkillComplianceRule.DescriptionTooLong, Severity.Medium,
                $"Skill description is {skill.Description.Length} characters — GA limits 'description' to {MaxDescriptionLength}.", "description");
        }

        // ── compatibility ──
        if (skill.CompatibilityLength is { } compatLength && compatLength > MaxCompatibilityLength)
        {
            Add(findings, label, SkillComplianceRule.CompatibilityTooLong, Severity.Low,
                $"Skill 'compatibility' is {compatLength} characters — GA limits it to {MaxCompatibilityLength}.", "compatibility");
        }

        // ── governance flags (AgentEval-owned, not GA rules) ──
        if (options.FlagScriptsForGovernance && skill.HasScripts)
        {
            Add(findings, label, SkillComplianceRule.ScriptRequiresGovernanceReview, Severity.Medium,
                $"Skill '{label}' exposes {skill.ScriptNames.Count} script(s) reachable via run_skill_script — recommend registering SkillScriptExecutionGate/SkillScriptApprovalGate (Phase 3) before allowing this skill in production.", null);
        }

        if (options.FlagUntrustedResourceSources
            && skill.HasResources
            && skill.SourceKind is SkillSourceKind.Mcp or SkillSourceKind.Custom)
        {
            Add(findings, label, SkillComplianceRule.ResourceFromUntrustedSource, Severity.Medium,
                $"Skill '{label}' resources originate from a {skill.SourceKind} source — third-party/untrusted content is an injection surface (see SkillInjectionAttack, Phase 3).", null);
        }

        if (skill.AllowedTools.Count > 0)
        {
            Add(findings, label, SkillComplianceRule.AllowedToolsExperimental, Severity.Low,
                $"Skill '{label}' uses the experimental 'allowed-tools' frontmatter field ({skill.AllowedTools.Count} tool(s) listed) — subject to change before GA stabilization.", "allowed-tools");
        }
    }

    private static void Add(
        List<SkillComplianceFinding> findings, string skillName, SkillComplianceRule rule, Severity severity, string message, string? field)
        => findings.Add(new SkillComplianceFinding(skillName, rule, severity, message, field));
}
