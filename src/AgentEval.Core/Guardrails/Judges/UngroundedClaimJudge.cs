// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A Tribunal flagship judge for <b>RAG faithfulness as a gate</b> — flags an answer claim not supported
/// by the retrieved context it was grounded in (Gatekeeper §C). Place run-post over
/// (retrieved-context, claim) pairs formatted via <see cref="UngroundedClaimRubric.FormatCase"/>.
/// <para><b>It does not lower the bar.</b> Calibration still decides: call <see cref="CalibrateAsync"/>,
/// check the report, and only wire it inline once <see cref="CalibrationReport.IsInlineReady"/>.</para>
/// </summary>
public static class UngroundedClaimJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:ungrounded-claim</c>).</summary>
    public const string Axis = "ungrounded-claim";

    /// <summary>Builds the judge as an <see cref="IChatGate"/> over a fast model.</summary>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<UngroundedClaimRubric>(new UngroundedClaimRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>The canonical deterministic baseline this judge must beat.</summary>
    public static IChatGate KeywordBaseline() => new KeywordOracleGate(policyName: "keyword-oracle:ungrounded-claim");

    /// <summary>The canonical both-directions gold set (24 ungrounded + 24 grounded).</summary>
    public static JudgeGoldSet GoldSet() => UngroundedClaimRubric.CalibrationGoldSet();

    /// <summary>Calibrate the judge against the canonical gold set and keyword-oracle baseline with a zero-missed-attacks bar.</summary>
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
