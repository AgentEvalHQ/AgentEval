// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Thrown by <c>UseGatekeeper</c> at agent-CONSTRUCTION time (not per-turn — see
/// <see cref="SkillGateConstructionCheck"/> for why Tier 1 needs no runtime seam) when SkillGate refuses to
/// build: a skill's content has changed since it was trust-pinned, or (under <see cref="SkillGateMode.Strict"/>)
/// a skill has no baseline entry at all. Fail-closed, the same pattern as
/// <see cref="PromptTemplateDriftException"/>/<see cref="UnprotectedHighRiskToolException"/>.
/// </summary>
public sealed class SkillDriftException : Exception
{
    /// <summary>
    /// The offending finding(s) — a mix of <see cref="ManifestDriftKind.Changed"/> (drift on a known skill,
    /// always blocking) and, under <see cref="SkillGateMode.Strict"/> only, <see cref="ManifestDriftKind.New"/>
    /// (a skill with no baseline entry at all).
    /// </summary>
    public IReadOnlyList<ManifestDriftFinding> Findings { get; }

    /// <summary>Creates the exception from the offending <paramref name="findings"/> (must be non-empty).</summary>
    public SkillDriftException(IReadOnlyList<ManifestDriftFinding> findings)
        : base(BuildMessage(findings))
    {
        Findings = findings;
    }

    private static string BuildMessage(IReadOnlyList<ManifestDriftFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var changed = findings.Where(f => f.Kind == ManifestDriftKind.Changed).Select(f => f.Key).ToArray();
        var unknown = findings.Where(f => f.Kind == ManifestDriftKind.New).Select(f => f.Key).ToArray();

        var parts = new List<string>();
        if (changed.Length > 0)
        {
            parts.Add(
                $"content changed since baseline for [{string.Join(", ", changed)}] (a possible tamper/rug-pull " +
                "on a skill this agent trusts by construction — re-review the change and, if legitimate, re-approve " +
                "via 'agenteval skills baseline approve <skill-name>')");
        }

        if (unknown.Length > 0)
        {
            parts.Add(
                $"no baseline entry at all for [{string.Join(", ", unknown)}] (SkillGateMode.Strict refuses to " +
                "trust an unrecognized skill by default — approve it via 'agenteval skills baseline approve " +
                "<skill-name>', or construct with SkillGateMode.Bootstrap to auto-trust unrecognized skills on " +
                "first sight)");
        }

        return "UseGatekeeper: SkillGate refused construction — " + string.Join("; ", parts) + ".";
    }
}
