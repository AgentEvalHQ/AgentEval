// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// Agent Skills Wave 1 (§4.3) — a representative seed of known repo-relative skill-directory conventions
/// used across the agent-tooling ecosystem. AgentEval-OWNED — an independent copy, not a dependency on any
/// third-party registry (e.g. Skillz) — extend this list independently of any upstream source; this is
/// deliberately NOT a claim of "supporting" every agent tool that has ever existed, just the well-known,
/// commonly-seen ones worth checking for by default.
/// </summary>
public static class AgentSkillDirectoryConventions
{
    /// <summary>
    /// Repo-relative directory conventions checked by <c>agenteval skills scan --repo</c>. Includes
    /// <c>.agents/skills</c> — the agentskills.io-recommended universal path — alongside several
    /// popular per-tool conventions and a bare, non-dot-prefixed <c>skills</c> fallback.
    /// </summary>
    public static IReadOnlyList<string> KnownConventions { get; } =
    [
        ".claude/skills",
        ".agents/skills",
        ".windsurf/skills",
        ".roo/skills",
        ".continue/skills",
        ".augment/skills",
        ".cursor/skills",
        ".codeium/skills",
        "skills",
    ];
}
