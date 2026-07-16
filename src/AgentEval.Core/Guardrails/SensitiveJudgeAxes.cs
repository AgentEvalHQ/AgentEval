// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// The set of Tribunal judge axes whose <see cref="GateVerdict.Reason"/> / <see cref="GateVerdict.Matches"/> may
/// themselves quote or paraphrase the sensitive content they detected — the offending phrase can BE the secret
/// (round-4 security rule). Shared by every consumer that writes or renders a judge verdict, so the axis list
/// and the "what counts as sensitive" definition can never drift between them: the CLI's <c>GateVerdictDto</c>
/// (<c>AgentEval.Cli</c>) and the runtime trace-writing path (<c>AgentEval.MAF.Gatekeeper</c>'s
/// <c>AgentEvalRunGateExtensions.RecordGate</c>) both key off this one list.
/// </summary>
public static class SensitiveJudgeAxes
{
    /// <summary>
    /// Axes whose evidence (<see cref="GateVerdict.Reason"/>, <see cref="GateVerdict.Matches"/>) must never be
    /// persisted verbatim — <c>exfiltration-intent</c> (the leaked data may be quoted in the judge's own
    /// rationale/spans) and <c>system-prompt-extraction</c> (the extracted prompt fragment may be quoted the
    /// same way).
    /// </summary>
    public static readonly IReadOnlySet<string> RedactAxes =
        new HashSet<string>(StringComparer.Ordinal) { "exfiltration-intent", "system-prompt-extraction" };

    /// <summary>
    /// The axis encoded in a <c>judge:&lt;axis&gt;</c> policy name (as <see cref="CompositeJudgeGate{TRubric}"/>
    /// names itself), or <see langword="null"/> for anything else (a deterministic gate, or a composed judge
    /// panel policy name that isn't a single axis).
    /// </summary>
    public static string? AxisOf(string policyName) =>
        policyName is not null && policyName.StartsWith("judge:", StringComparison.Ordinal)
            ? policyName["judge:".Length..]
            : null;

    /// <summary>True when <paramref name="policyName"/> encodes an axis in <see cref="RedactAxes"/>.</summary>
    public static bool IsSensitive(string policyName)
    {
        var axis = AxisOf(policyName);
        return axis is not null && RedactAxes.Contains(axis);
    }
}
