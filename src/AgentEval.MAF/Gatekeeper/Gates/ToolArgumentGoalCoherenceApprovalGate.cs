// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Approval-flow half of the tool-argument/goal-coherence judge (see
/// <see cref="AgentEval.Guardrails.Judges.ToolArgumentGoalCoherenceJudge"/> for the axis, rubric, and why this
/// is the ONLY seam that can act on this judge's verdict for a specific pending tool call today — the
/// deterministic hard-block seam (<c>UseAgentEvalToolGate</c>) rejects LLM-cost gates at construction, and
/// tool-gate shadow mode does not exist yet). Auto-approves only on a confident-coherent verdict; a
/// confident-incoherent verdict AND an inconclusive/timeout verdict both escalate to a human — this gate never
/// distinguishes "definitely bad" from "not sure" in its outcome, because both already route to the same safe
/// place (a human decides), matching <see cref="IToolApprovalGate"/>'s own fail-closed philosophy.
/// </summary>
/// <remarks>
/// <b>Fixed goal.</b> The goal text is supplied once, at construction — a single instance is meant for one run
/// with one stated goal, not reused across a long-lived agent serving multiple different user goals over its
/// lifetime (build a fresh gate per goal instead). See <see cref="ToolArgumentGoalCoherenceRubric"/>'s own
/// remarks for why this is a disclosed v1 scope choice, not an oversight.
/// </remarks>
public sealed class ToolArgumentGoalCoherenceApprovalGate : IToolApprovalGate
{
    // Relaxed encoder: default JSON escaping turns some characters into \uXXXX, which would put an escaped
    // surface in front of the judge rather than the literal text a human/attacker actually wrote.
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IChatGate _judge;
    private readonly string _goal;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the gate from a fast model and the fixed goal it judges every pending call against.</summary>
    /// <param name="fastModel">The fast/mini chat model the underlying judge calls.</param>
    /// <param name="goal">The agent's stated goal for this run. Must be non-empty.</param>
    /// <param name="options">Judge gate options (timeout, block threshold, fail-closed-on-inconclusive). Defaults applied when null.</param>
    /// <param name="policyName">Evidence / policy name. Defaults to <c>judge:tool-argument-goal-coherence</c>.</param>
    public ToolArgumentGoalCoherenceApprovalGate(IChatClient fastModel, string goal, JudgeGateOptions? options = null, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("goal must be non-empty.", nameof(goal));
        }

        _judge = AgentEval.Guardrails.Judges.ToolArgumentGoalCoherenceJudge.Create(fastModel, options, cache: true);
        _goal = goal;
        PolicyName = string.IsNullOrWhiteSpace(policyName) ? $"judge:{AgentEval.Guardrails.Judges.ToolArgumentGoalCoherenceJudge.Axis}" : policyName;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        string argumentsJson;
        try
        {
            argumentsJson = JsonSerializer.Serialize(call.Arguments, ScanOptions);
        }
        catch (NotSupportedException)
        {
            return false;   // cannot inspect the arguments ⇒ escalate (fail-closed)
        }

        var text = ToolArgumentGoalCoherenceRubric.FormatCase(_goal, call.Name, argumentsJson);
        var verdict = await _judge.InspectAsync(text, cancellationToken).ConfigureAwait(false);
        return verdict.Action == GateAction.Allow;
    }
}
