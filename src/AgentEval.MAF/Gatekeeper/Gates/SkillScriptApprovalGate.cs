// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Approval gate for MAF Agent Skills (Phase 3, Part B — design doc §6.2): auto-approves the read-only
/// skill tools (<c>load_skill</c> / <c>read_skill_resource</c>) and escalates <c>run_skill_script</c> to a
/// human UNLESS its script is on a per-script trust allowlist.
/// </summary>
/// <remarks>
/// <b>Why not just MAF's native <c>AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule</c>?</b> That coarse
/// rule ("auto-approve load/read, escalate run") is exactly what this gate's baseline behavior matches —
/// if that's all you need, use the native rule. This gate's genuine additive value is PER-SCRIPT trust
/// allowlisting (<see cref="_trustedScripts"/>): the native rule is all-or-nothing at TOOL granularity,
/// whereas this gate can auto-approve <c>run_skill_script</c> for a specific trusted script while still
/// escalating every other script. The deterministic HARD stop is <see cref="SkillScriptExecutionGate"/> at
/// the FICC seam; this gate is the human-in-the-loop layer above it — see the design doc's Posture A/B
/// composition-ordering note (§6.2) for why the two must not be conflated.
/// </remarks>
public sealed class SkillScriptApprovalGate : IToolApprovalGate
{
    private readonly HashSet<string> _trustedScripts;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the gate.</summary>
    /// <param name="trustedScripts">
    /// Script identifiers that may auto-approve <c>run_skill_script</c> without human escalation. Same
    /// value-based candidate matching as <see cref="SkillScriptExecutionGate"/> (bare <c>scriptName</c> or
    /// a <c>skillName</c>/<c>scriptName</c> combined identifier). <see langword="null"/> or empty means NO
    /// script auto-approves — every <c>run_skill_script</c> call escalates (matches the native
    /// <c>ReadOnlyToolsAutoApprovalRule</c> behavior exactly).
    /// </param>
    /// <param name="policyName">Optional override of the recorded policy name (default <c>"SkillScriptApprovalGate"</c>).</param>
    public SkillScriptApprovalGate(IEnumerable<string>? trustedScripts = null, string? policyName = null)
    {
        _trustedScripts = new HashSet<string>(trustedScripts ?? [], StringComparer.OrdinalIgnoreCase);
        PolicyName = policyName ?? "SkillScriptApprovalGate";
    }

    /// <inheritdoc/>
    public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // load_skill / read_skill_resource are read-only — always auto-approvable (matches the native
        // ReadOnlyToolsAutoApprovalRule).
        if (string.Equals(call.Name, SkillToolNames.LoadSkill, StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.Name, SkillToolNames.ReadSkillResource, StringComparison.OrdinalIgnoreCase))
        {
            return new ValueTask<bool>(true);
        }

        if (!string.Equals(call.Name, SkillToolNames.RunSkillScript, StringComparison.OrdinalIgnoreCase))
        {
            // Not a skill tool at all — this gate has no opinion; fail closed (escalate) rather than
            // silently auto-approving an unrelated tool it was never asked to judge.
            return new ValueTask<bool>(false);
        }

        if (_trustedScripts.Count == 0 || call.Arguments is null || call.Arguments.Count == 0)
        {
            return new ValueTask<bool>(false);   // no trust list, or nothing to check ⇒ escalate
        }

        foreach (var candidate in Candidates(call.Arguments))
        {
            if (_trustedScripts.Contains(candidate))
            {
                return new ValueTask<bool>(true);
            }
        }

        return new ValueTask<bool>(false);   // untrusted script ⇒ escalate to a human, never auto-approve
    }

    private static IEnumerable<string> Candidates(IDictionary<string, object?> arguments)
    {
        var stringValues = new List<string>();
        foreach (var value in arguments.Values)
        {
            if (value is string s && !string.IsNullOrEmpty(s))
            {
                stringValues.Add(s);
                yield return s;
            }
        }

        for (var i = 0; i < stringValues.Count; i++)
        {
            for (var j = 0; j < stringValues.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                yield return $"{stringValues[i]}/{stringValues[j]}";
            }
        }
    }
}
