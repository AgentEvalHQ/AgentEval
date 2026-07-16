// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;

namespace AgentEval.Assertions;

/// <summary>
/// Zero-cost skill contract assertions — no agent, no LLM, no trace. Wraps the pure
/// <see cref="SkillComplianceValidator"/> (Phase 2) as a one-line unit-test assertion, for a build-time
/// "is this skill well-formed" check independent of any live run. Design catalog §10.4, item 10.
/// </summary>
public static class SkillContractAssertions
{
    /// <summary>
    /// Asserts <paramref name="manifest"/> has no <see cref="Severity.High"/> compliance finding (GA
    /// SKILL.md rule violations that would prevent the skill from loading). Throws
    /// <see cref="AgentEvalAssertionException"/> with the specific finding(s) on failure.
    /// </summary>
    /// <param name="manifest">The skill manifest to check.</param>
    /// <param name="options">Optional scan options (defaults apply governance flags too, but only High findings fail this assertion).</param>
    public static void AssertSkillWellFormed(SkillManifest manifest, SkillScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var report = SkillComplianceValidator.Validate([manifest], options);
        var highFindings = report.Findings.Where(f => f.Severity == Severity.High).ToList();

        if (highFindings.Count > 0)
        {
            var messages = string.Join("; ", highFindings.Select(f => $"{f.Rule}: {f.Message}"));
            throw AgentEvalAssertionException.Create(
                $"Skill '{manifest.Name}' is not well-formed ({highFindings.Count} High finding(s)): {messages}",
                because: null);
        }
    }
}
