// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic hard gate on <c>run_skill_script</c> (MAF Agent Skills Phase 3, Part B —
/// <c>strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md</c> §6.2). Blocks a
/// <c>run_skill_script</c> call whose script identifier is not on the allowlist. <see cref="GateCost.PureCode"/>.
/// <see cref="MinimumPolicy"/> is <see cref="ToolGatePolicy.ReplaceResult"/> — its whole purpose is to STOP
/// unapproved code execution, so it must not be silently registered under <see cref="ToolGatePolicy.WarnOnly"/>.
/// </summary>
/// <remarks>
/// <b>Value-based, key-agnostic matching (same mitigation as the P1 assertions/P2 scanner).</b> The script
/// identifier is pulled out of <see cref="GatedToolCall.Arguments"/> without assuming a specific argument
/// KEY (a future MAF rename must not silently fail open): every string-shaped argument VALUE is a
/// candidate, plus the "/"-joined pair of any two distinct string values (covering an allowlist entry
/// shaped like <c>"{skillName}/{scriptName}"</c> as well as a bare <c>scriptName</c> value). A call whose
/// script identifier cannot be matched against the allowlist — an unrecognized value, or a missing/empty
/// argument set — BLOCKS (fail-closed). A wrong-key guess must never fail open.
/// </remarks>
public sealed class SkillScriptExecutionGate : IToolGate
{
    private readonly HashSet<string> _allowedScripts;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

    /// <summary>Creates the gate.</summary>
    /// <param name="allowedScripts">
    /// The allowlisted script identifiers. Case-insensitive exact match against a candidate value derived
    /// from the call's arguments (see remarks) — e.g. <c>"scripts/summarize.csx"</c> (the bare
    /// <c>scriptName</c> argument value) or <c>"expense-report/scripts/summarize.csx"</c> (a
    /// <c>skillName</c>/<c>scriptName</c> combined identifier, for disambiguating same-named scripts
    /// across different skills).
    /// </param>
    /// <param name="policyName">Optional override of the recorded policy name (default <c>"SkillScriptExecutionGate"</c>).</param>
    public SkillScriptExecutionGate(IEnumerable<string> allowedScripts, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(allowedScripts);
        _allowedScripts = new HashSet<string>(allowedScripts, StringComparer.OrdinalIgnoreCase);
        if (_allowedScripts.Count == 0)
        {
            throw new ArgumentException("At least one allowed script identifier is required.", nameof(allowedScripts));
        }

        PolicyName = policyName ?? "SkillScriptExecutionGate";
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Only run_skill_script calls are in scope — everything else is an immediate allow.
        if (!string.Equals(call.FunctionName, SkillToolNames.RunSkillScript, StringComparison.OrdinalIgnoreCase))
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            // No arguments at all ⇒ cannot verify which script is being run ⇒ fail closed.
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                "run_skill_script called with no arguments — cannot verify the script identifier, failing closed"));
        }

        foreach (var candidate in Candidates(call.Arguments))
        {
            if (_allowedScripts.Contains(candidate))
            {
                return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
            }
        }

        // Deliberately does not echo the attempted script identifier — only that it was not on the allowlist
        // (mirrors TaintTrackingGate/MonetaryLimitGate's "never leak the attempted value" discipline).
        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
            "run_skill_script's script identifier is not on the allowlist"));
    }

    // Every string-shaped argument value, plus the "/"-joined pair of any two DISTINCT string values (in
    // both orders — value-based/key-agnostic, so this does not assume which key is skillName vs scriptName).
    private static IEnumerable<string> Candidates(IReadOnlyDictionary<string, object?> arguments)
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
