// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How <c>UseGatekeeper</c>'s SkillGate construction-time check handles a skill with NO entry at all in the
/// pinned baseline — the one case that is genuinely mode-dependent (a CHANGED fingerprint for a skill present
/// in both sides always blocks, regardless of mode; see <see cref="SkillGateConstructionCheck"/>).
/// </summary>
public enum SkillGateMode
{
    /// <summary>
    /// Default. An unrecognized skill (no baseline entry) refuses construction — the same fail-closed
    /// doctrine <c>CompositeJudgeGate</c>/<c>GateCalibrationHarness.IsInlineReady</c> already apply
    /// elsewhere in this repo: an unknown skill is not innocent-until-proven-guilty. Recommended for CI and
    /// production.
    /// </summary>
    Strict,

    /// <summary>
    /// Explicit opt-in. An unrecognized skill is trusted on first sight — auto-added to the baseline (both
    /// the structural fingerprint and, when the skill's folder is resolvable, the content hash), then the
    /// UPDATED baseline is written back to the same file <c>UseGatekeeper</c> loaded it from, so the NEXT
    /// construction (next process start) can detect drift against it. This is the direct enforcement-side
    /// counterpart of the skills-scan CLI's <c>MatchesPreviouslyVettedCopy</c> trust-on-first-use detector —
    /// same semantic, made load-bearing here instead of purely informational.
    /// <para>
    /// Writes to disk during agent construction — a real side effect, not just a read. Intended for local
    /// development / a controlled first-ever rollout, not for production processes with concurrent or
    /// frequent re-construction against the same baseline file (no file locking is applied; a race between
    /// two processes bootstrapping the same path concurrently can lose one process's newly-trusted entries).
    /// </para>
    /// </summary>
    Bootstrap,
}
