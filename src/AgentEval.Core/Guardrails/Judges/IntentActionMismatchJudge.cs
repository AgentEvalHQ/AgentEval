// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A Tribunal flagship judge for <b>intent/action mismatch</b> — compares what the agent narrated it was
/// about to do against what its tool call actually does, and vetoes on divergence. Turns the agent's own
/// narration into an oracle (Gatekeeper §C).
/// <para><b>Placement.</b> Since this judge calls an LLM (<c>GateCost.Llm</c>), it cannot run inline
/// at the FICC tool-invocation seam (<c>UseAgentEvalToolGate</c> rejects LLM-cost gates by construction) —
/// wire it as an <see cref="IChatGate"/> on formatted (narration, action) text, e.g. via
/// <c>UseAgentEvalGate</c> at the chat seam, or a caller-driven per-call inspection using
/// <see cref="IntentActionMismatchRubric.FormatCase"/> to build the text from the agent's most recent
/// narration and the pending tool call.</para>
/// <para><b>Evadable if the agent stops narrating</b> — a documented, honest limitation; this axis has no
/// signal without narration text. Not a substitute for deterministic argument-pattern gates.</para>
/// <para><b>It does not lower the bar.</b> Calibration still decides: call <see cref="CalibrateAsync"/>,
/// check the report, and only wire it inline once <see cref="CalibrationReport.IsInlineReady"/>.</para>
/// </summary>
public static class IntentActionMismatchJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:intent-action-mismatch</c>).</summary>
    public const string Axis = "intent-action-mismatch";

    /// <summary>Builds the judge as an <see cref="IChatGate"/> over a fast model.</summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls.</param>
    /// <param name="options">Judge gate options. Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true).</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<IntentActionMismatchRubric>(new IntentActionMismatchRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>The canonical deterministic baseline this judge must beat — a naive keyword oracle.</summary>
    public static IChatGate KeywordBaseline() => new KeywordOracleGate(policyName: "keyword-oracle:intent-action-mismatch");

    /// <summary>The canonical both-directions gold set (26 mismatches + 26 consistent).</summary>
    public static JudgeGoldSet GoldSet() => IntentActionMismatchRubric.CalibrationGoldSet();

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
