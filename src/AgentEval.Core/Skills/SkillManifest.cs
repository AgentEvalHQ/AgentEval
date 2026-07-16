// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// Where a skill's content originates. Drives trust-boundary decisions in the compliance validator
/// (<see cref="SkillComplianceRule.ResourceFromUntrustedSource"/>) — an <see cref="Mcp"/> or
/// <see cref="Custom"/> source is model-controlled/third-party text, a stronger injection surface than
/// a repo-local <see cref="File"/> skill.
/// </summary>
public enum SkillSourceKind
{
    /// <summary>A <c>SKILL.md</c> directory on disk (<c>AgentFileSkillsSource</c>).</summary>
    File,

    /// <summary>An in-memory/inline skill defined in code (<c>AgentInMemorySkillsSource</c> / <c>AgentInlineSkill</c>).</summary>
    InMemory,

    /// <summary>A strongly-typed class-based skill (<c>AgentClassSkill&lt;T&gt;</c>).</summary>
    Class,

    /// <summary>An MCP-served skill (experimental <c>Microsoft.Agents.AI.Mcp</c>) — third-party, untrusted by default.</summary>
    Mcp,

    /// <summary>Any other/custom <c>AgentSkillsSource</c> implementation not covered above.</summary>
    Custom,
}

/// <summary>
/// AgentEval-owned, MAF-free projection of a skill's inspectable surface. The <c>AgentEval.MAF.Skills</c>
/// adapter maps a live <c>AgentSkill</c> to this DTO so the pure <see cref="SkillComplianceValidator"/>
/// (in <c>AgentEval.Core</c>, which does not — and must not — reference <c>Microsoft.Agents.AI</c>) never
/// sees a MAF type. See the design doc's "the build enforces the boundary" principle.
/// </summary>
/// <param name="Name">The skill's declared name (SKILL.md frontmatter <c>name</c>, or the in-memory/class skill's id).</param>
/// <param name="Description">The skill's declared description (frontmatter <c>description</c>).</param>
/// <param name="ResourceNames">Logical resource names the skill exposes (for <c>read_skill_resource</c>).</param>
/// <param name="ScriptNames">Logical script names the skill exposes (for <c>run_skill_script</c>).</param>
/// <param name="CompatibilityLength">Length of the frontmatter <c>compatibility</c> field, if present; <see langword="null"/> if absent.</param>
/// <param name="AllowedTools">The experimental frontmatter <c>allowed-tools</c> list, if present.</param>
/// <param name="SourceKind">Where this skill's content originates — drives trust-boundary checks.</param>
/// <param name="ParentDirectoryName">
/// For <see cref="SkillSourceKind.File"/> skills, the name of the directory the skill was loaded from
/// (SKILL.md GA rule: <see cref="Name"/> must match this). <see langword="null"/> for non-file sources.
/// </param>
public sealed record SkillManifest(
    string Name,
    string? Description,
    IReadOnlyList<string> ResourceNames,
    IReadOnlyList<string> ScriptNames,
    int? CompatibilityLength,
    IReadOnlyList<string> AllowedTools,
    SkillSourceKind SourceKind,
    string? ParentDirectoryName)
{
    /// <summary>Convenience: does this skill expose any scripts (<c>run_skill_script</c> surface)?</summary>
    public bool HasScripts => ScriptNames.Count > 0;

    /// <summary>Convenience: does this skill expose any resources (<c>read_skill_resource</c> surface)?</summary>
    public bool HasResources => ResourceNames.Count > 0;
}
