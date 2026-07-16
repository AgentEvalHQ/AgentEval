// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// Severity of a <see cref="SkillComplianceFinding"/>. Scoped to <c>AgentEval.Skills</c> — deliberately
/// NOT a shared cross-namespace type (<c>AgentEval.Core</c> does not reference <c>AgentEval.RedTeam</c>,
/// which owns its own unrelated <c>Severity</c> enum for compliance reporters). Zero coupling.
/// </summary>
public enum Severity
{
    /// <summary>Informational — a soft recommendation, not a defect.</summary>
    Low,

    /// <summary>A real quality/governance gap, but the skill still functions.</summary>
    Medium,

    /// <summary>A GA rule violation that prevents the skill from loading, or a real trust-boundary risk.</summary>
    High,
}

/// <summary>The catalogued compliance rules a <see cref="SkillManifest"/> is checked against.</summary>
public enum SkillComplianceRule
{
    /// <summary><c>name</c> is missing/empty.</summary>
    NameMissing,

    /// <summary><c>name</c> exceeds the GA 64-character limit.</summary>
    NameTooLong,

    /// <summary><c>name</c> contains characters outside <c>[a-z0-9-]</c>.</summary>
    NameInvalidChars,

    /// <summary><c>name</c> contains consecutive hyphens (GA disallows <c>--</c>).</summary>
    NameConsecutiveHyphens,

    /// <summary>For a file-sourced skill, <c>name</c> does not match its parent directory name (GA requirement).</summary>
    NameMismatchesDirectory,

    /// <summary><c>description</c> is missing/empty.</summary>
    DescriptionMissing,

    /// <summary><c>description</c> exceeds the GA 1024-character limit.</summary>
    DescriptionTooLong,

    /// <summary><c>compatibility</c> exceeds the GA 500-character limit.</summary>
    CompatibilityTooLong,

    /// <summary>The skill exposes <c>run_skill_script</c> surface — recommends the Phase 3 exec-governance gate.</summary>
    ScriptRequiresGovernanceReview,

    /// <summary>The skill's resources originate from an MCP/Custom (untrusted, third-party) source — an injection surface.</summary>
    ResourceFromUntrustedSource,

    /// <summary>The skill uses the experimental <c>allowed-tools</c> frontmatter field.</summary>
    AllowedToolsExperimental,

    /// <summary>
    /// A skill folder exists on disk (has its own <c>SKILL.md</c>) but MAF's own
    /// <c>AgentFileSkillsSource.GetSkillsAsync()</c> silently excluded it from discovery — it will never
    /// load into any agent. Distinct from every rule above: those describe a problem with an otherwise-
    /// working skill; this means the skill is <em>non-functional as authored</em>. See
    /// <c>strategy/FutureFeatures/Skills/Skill-Discovery-Exclusion-Detection-Design.md</c>.
    /// </summary>
    SkillExcludedFromDiscovery,
}

/// <summary>One rule violation (or informational flag) found for one skill.</summary>
/// <param name="SkillName">The skill the finding is about.</param>
/// <param name="Rule">Which rule fired.</param>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Field">The frontmatter field implicated, if any (e.g. <c>"name"</c>, <c>"description"</c>).</param>
public sealed record SkillComplianceFinding(
    string SkillName, SkillComplianceRule Rule, Severity Severity, string Message, string? Field);

/// <summary>Aggregate counts across every skill scanned.</summary>
/// <param name="SkillCount">Total skills scanned.</param>
/// <param name="WithResources">Skills exposing at least one resource.</param>
/// <param name="WithScripts">Skills exposing at least one script.</param>
/// <param name="StageHistogram">
/// Reachability histogram over the three OBSERVABLE progressive-disclosure stages this scan can see
/// statically — <c>"load"</c> (always reachable, every skill), <c>"read"</c> (skills with resources),
/// <c>"run"</c> (skills with scripts). "advertise" is a system-prompt listing, not a tool-call stage —
/// consistent with <see cref="AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric"/>'s own
/// observable-stages-only discipline, this histogram never fabricates an advertise count.
/// </param>
/// <param name="SilentlyExcludedCount">
/// How many on-disk skill folders MAF's own discovery silently excluded before this scan ever saw them
/// (<see cref="SkillComplianceRule.SkillExcludedFromDiscovery"/>) — surfaced as its own field, not buried
/// in <see cref="SkillCount"/>, because these folders were never counted as scanned skills in the first
/// place. Defaults to 0 for any report built the pre-Item-5 way (e.g. hand-built test fixtures).
/// </param>
public sealed record SkillCoverageSummary(
    int SkillCount,
    int WithResources,
    int WithScripts,
    IReadOnlyDictionary<string, int> StageHistogram,
    int SilentlyExcludedCount = 0);

/// <summary>The full result of a compliance scan: every finding plus the coverage summary.</summary>
/// <param name="Findings">Every finding across every scanned skill.</param>
/// <param name="Coverage">Aggregate coverage counts.</param>
public sealed record SkillComplianceReport(IReadOnlyList<SkillComplianceFinding> Findings, SkillCoverageSummary Coverage)
{
    /// <summary>
    /// <see langword="true"/> only when no finding is <see cref="Severity.High"/>. A skill can carry
    /// <see cref="Severity.Low"/>/<see cref="Severity.Medium"/> findings and still be compliant.
    /// </summary>
    public bool IsCompliant => Findings.All(f => f.Severity < Severity.High);
}

/// <summary>Tuning for <see cref="SkillComplianceValidator.Validate"/>.</summary>
public sealed class SkillScanOptions
{
    /// <summary>Flag skills with scripts for Phase 3 exec-governance review. Default <see langword="true"/>.</summary>
    public bool FlagScriptsForGovernance { get; init; } = true;

    /// <summary>Flag resources originating from an untrusted (MCP/Custom) source. Default <see langword="true"/>.</summary>
    public bool FlagUntrustedResourceSources { get; init; } = true;

    /// <summary>Enforce the GA "name matches parent directory" rule for file-sourced skills. Default <see langword="true"/>.</summary>
    public bool EnforceNameMatchesDirectory { get; init; } = true;
}
