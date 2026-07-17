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

    /// <summary>
    /// Agent Skills Wave 2 — <c>agenteval skills scan --repo</c> found the SAME skill name present under two
    /// or more different directory conventions (e.g. <c>.claude/skills/foo</c> and <c>.cursor/skills/foo</c>)
    /// with DIFFERENT <see cref="SkillContentHasher.HashSkillFolder"/> content hashes. Which copy actually
    /// loads depends on which agent tool reads it — could be intentional per-tool customization, or could be
    /// one location silently drifting (or being poisoned) while another stays trusted.
    /// <para><b>Deliberately <see cref="Severity.Medium"/>, not High</b> — a governance signal to
    /// investigate, not automatically a defect: the divergence may be deliberate per-tool customization,
    /// which is a legitimate, common pattern this rule must not punish. Matches this same file's own
    /// precedent (<see cref="ResourceFromUntrustedSource"/> is also Medium despite naming an injection-surface
    /// risk in its own doc comment) rather than <see cref="SkillExcludedFromDiscovery"/>'s High (which fires
    /// only when a skill is provably non-functional, not merely ambiguous).</para>
    /// <para><b>Known scoping limits, not yet closed:</b> the competing locations/hashes are reported only in
    /// the finding's human-readable <see cref="SkillComplianceFinding.Message"/> (hashes truncated to 8 hex
    /// chars for display) — a machine consumer (CI script, SARIF tool) cannot extract them without
    /// string-parsing. A container-mode skill nested 2 directory levels deep under the SAME convention can
    /// share a bare directory name with an unrelated sibling, producing an ambiguous location label. Both are
    /// accepted, disclosed gaps for Wave 2, not silently-missed cases.</para>
    /// </summary>
    CrossLocationContentDrift,

    /// <summary>
    /// Agent Skills Wave 2, trust-on-first-use — <c>agenteval skills scan --check-baseline</c> found this
    /// skill's current <see cref="SkillContentHasher.HashSkillFolder"/> content hash matches a hash already
    /// present in the baseline ledger (<see cref="ISkillBaselineStore"/>) for the SAME skill name — i.e. this
    /// exact copy was already captured and vetted in a prior scan. Purely informational
    /// (<see cref="Severity.Low"/>) — a positive "no drift since last seen" signal, not a problem.
    /// </summary>
    MatchesPreviouslyVettedCopy,

    /// <summary>
    /// <c>agenteval skills scan --manifest-baseline &lt;file&gt;</c> found this skill's current
    /// <see cref="AgentEval.Skills.SkillManifestPoisoningGate.Fingerprint"/> differs from the pin captured
    /// in a prior <c>--save-manifest-baseline</c> run — the skill's manifest content (name, description,
    /// resource/script inventory, allowed-tools, compatibility) changed since it was reviewed and trust-time
    /// pinned. A real rug-pull candidate: a previously-approved skill silently changing after approval is a
    /// live trust-boundary breach, so this is <see cref="Severity.High"/> — the same severity
    /// <see cref="AgentEval.Skills.SkillSecurityIndex"/>'s own <c>ChangedManifestPenalty</c> already treats a
    /// changed manifest with. Distinct from <see cref="CrossLocationContentDrift"/> (compares the SAME scan's
    /// multiple locations against each other) — this compares ONE scan against an earlier, deliberately
    /// pinned trust-time snapshot, mirroring the RedTeam baseline/diff CI pattern.
    /// </summary>
    ManifestChangedSinceBaseline,
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
