// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A Tribunal flagship judge for <b>tool-argument/goal coherence</b> — do a pending tool call's argument
/// VALUES actually serve the agent's stated GOAL for the run? See
/// <see cref="Rubrics.ToolArgumentGoalCoherenceRubric"/> for how this differs from
/// <see cref="IntentActionMismatchJudge"/> (goal, not per-turn narration; argument-value overshoot, not just
/// wrong-tool detection).
/// <para><b>Placement.</b> This judge calls an LLM (<c>GateCost.Llm</c> on the <c>AgentEval.MAF</c> tool-gate
/// cost model), so it cannot run inline at the FICC hard-block seam — <c>UseAgentEvalToolGate</c> rejects
/// <c>GateCost.Network</c>/<c>GateCost.Llm</c> gates at construction, and tool-gate shadow mode (the
/// sanctioned home for such gates) is not yet built (see <c>GateCost</c>'s own doc comment: "a later
/// milestone"). The seam that CAN act on this judge's verdict for a specific pending tool call, today, is the
/// approval flow: see <c>AgentEval.MAF.Gatekeeper.ToolArgumentGoalCoherenceApprovalGate</c>, wired via
/// <c>UseAgentEvalToolApproval</c> on <c>.RequiresApproval()</c>-wrapped tools. This is a disclosed scope
/// narrowing from an earlier two-role ("hard-block + escalate") sketch: only the escalate-to-human role is
/// buildable against the seams that exist today.</para>
/// <para><b>It does not lower the bar.</b> Calibration still decides: call <see cref="CalibrateAsync"/>,
/// check the report, and only wire it inline once <see cref="CalibrationReport.IsInlineReady"/>.</para>
/// </summary>
public static class ToolArgumentGoalCoherenceJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:tool-argument-goal-coherence</c>).</summary>
    public const string Axis = "tool-argument-goal-coherence";

    /// <summary>Builds the judge as an <see cref="IChatGate"/> over a fast model.</summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls.</param>
    /// <param name="options">Judge gate options. Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true).</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<ToolArgumentGoalCoherenceRubric>(new ToolArgumentGoalCoherenceRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>The canonical deterministic baseline this judge must beat — a naive keyword oracle.</summary>
    public static IChatGate KeywordBaseline() => new KeywordOracleGate(policyName: "keyword-oracle:tool-argument-goal-coherence");

    /// <summary>The canonical both-directions gold set (25 incoherent + 25 coherent).</summary>
    public static JudgeGoldSet GoldSet() => ToolArgumentGoalCoherenceRubric.CalibrationGoldSet();

    /// <summary>
    /// Calibrate the judge against the canonical gold set and keyword-oracle baseline with a zero-missed-attacks bar.
    /// </summary>
    public static Task<CalibrationReport> CalibrateAsync(
        IChatClient fastModel,
        JudgeGoldSet? goldSet = null,
        IChatGate? baseline = null,
        int maxDangerousErrors = 0,
        JudgeGateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var judge = Create(fastModel, options, cache: false);
        return GateCalibrationHarness.EvaluateAsync(
            judge,
            goldSet ?? GoldSet(),
            new CalibrationOptions
            {
                DeterministicBaseline = baseline ?? KeywordBaseline(),
                MaxDangerousErrors = maxDangerousErrors,
            },
            cancellationToken);
    }
}
