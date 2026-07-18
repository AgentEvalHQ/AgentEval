// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Guardrails;

/// <summary>
/// Configuration for <see cref="FleetCorrelator"/> — the false-positive guards, tunable. See that type's own
/// remarks for the mechanism these knobs control.
/// <para><b>Experimental:</b> shipped 2026-07-18, no real-world calibration data yet — the defaults are
/// reasoned, not measured (see <see cref="FleetCorrelator"/>'s remarks). Suppress via
/// <c>&lt;NoWarn&gt;$(NoWarn);AGENTEVAL_GATEKEEPER_PREVIEW001&lt;/NoWarn&gt;</c> once you've read this note.</para>
/// </summary>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed class FleetCorrelatorOptions
{
    /// <summary>
    /// Minimum per-verdict <see cref="GateVerdict.Confidence"/> to count as a "soft signal" worth correlating.
    /// Default 0.4 — deliberately well below any individual gate's own block threshold (typically ~0.7-0.8), a
    /// separate, higher-friction-to-misconfigure knob from that threshold.
    /// </summary>
    public double SoftSignalFloor { get; init; } = 0.4;

    /// <summary>
    /// The bounded trailing-turn window correlated evidence must fall within. Default 5. Unbounded
    /// session-lifetime accumulation would let irrelevant early-conversation noise combine with unrelated
    /// late-conversation noise — an actual attack, by nature, escalates over a short span.
    /// </summary>
    public int WindowTurns { get; init; } = 5;

    /// <summary>
    /// Minimum number of DISTINCT families (see <see cref="FamilyOf"/>) that must each report a qualifying
    /// signal before <see cref="FleetCorrelator.CheckCorrelation"/> escalates. Default and floor: 2 — the
    /// single biggest lever against noise; a chatty single family firing repeatedly proves nothing new on its
    /// own. Must be at least 2 (enforced by <see cref="FleetCorrelator"/>'s constructor) — 1 would mean a
    /// single low-confidence signal alone escalates, defeating the entire correlation premise.
    /// </summary>
    public int MinDistinctFamilies { get; init; } = 2;

    /// <summary>
    /// Maps a gate's <see cref="GateVerdict.PolicyName"/> to its correlation "family" identity — two verdicts
    /// in the SAME family never count as 2 distinct signals, no matter how many times the family fires.
    /// Defaults to identity (a <see cref="GateVerdict.PolicyName"/> is already stable and namespaced per
    /// independent detection mechanism, e.g. <c>judge:{axis}</c>, <c>prompt-template-drift</c>,
    /// <c>tool-result-size-anomaly</c>) — override only to group multiple policy names under one family.
    /// </summary>
    public Func<string, string> FamilyOf { get; init; } = static policyName => policyName;
}
