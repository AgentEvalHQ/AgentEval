// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// A reconstructable "why" behind a <see cref="GateVerdict"/> — which rule fired, what evidence it saw, and
/// (for a threshold-based gate, e.g. a judge) the threshold it was compared against versus the actual value
/// observed. Additive and optional: a gate that doesn't populate <see cref="GateVerdict.Provenance"/> behaves
/// exactly as before — this is a richer alternative to <see cref="GateVerdict.Reason"/>'s free-text string,
/// not a replacement for it.
/// </summary>
/// <param name="RuleName">
/// Stable identifier for the specific rule/detector that produced this verdict — e.g. a judge rubric's axis
/// (<c>"judge:indirect-injection"</c>) or a deterministic gate's policy name. Distinct from
/// <see cref="GateVerdict.PolicyName"/> when one policy composes several named rules internally; equal to it
/// for a gate that IS one rule.
/// </param>
/// <param name="Evidence">
/// The concrete evidence this rule's decision rests on — e.g. matched spans, keyword hits, or a judge's own
/// rationale sentence. Empty (not null) when the rule found nothing notable.
/// </param>
/// <param name="Threshold">
/// The threshold this rule's decision was compared against, when the rule is threshold-based (e.g. a judge's
/// <c>BlockThreshold</c>). Null for a rule with no numeric threshold (most deterministic regex/keyword gates).
/// </param>
/// <param name="ActualValue">
/// The actual measured value compared against <paramref name="Threshold"/> (e.g. a judge's confidence). Null
/// under the same conditions as <paramref name="Threshold"/> — the two are populated or omitted together.
/// </param>
/// <param name="Contributing">
/// Sub-provenance chains this verdict was derived from, for a gate that aggregates other gates' findings
/// (e.g. a panel, or a fleet-level correlation over several turns' verdicts). Null for a leaf rule with no
/// sub-decisions — most gates.
/// </param>
public sealed record GateProvenance(
    string RuleName,
    IReadOnlyList<string> Evidence,
    double? Threshold = null,
    double? ActualValue = null,
    IReadOnlyList<GateProvenance>? Contributing = null)
{
    /// <summary>A provenance chain with no evidence, threshold, or contributing chain — just the rule name.</summary>
    public static GateProvenance ForRule(string ruleName) => new(ruleName, Array.Empty<string>());
}
