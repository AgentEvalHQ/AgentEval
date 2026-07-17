// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// The calibratable CORE of the Crescendo-trajectory defense — a single-axis "does this newest turn escalate
/// relative to the established conversation?" judge, built from <see cref="CrescendoTrajectoryRubric"/>. See
/// that rubric's doc comment for why the axis is shaped as a per-turn-shift question rather than a whole-
/// conversation score.
/// <para><b>Not the whole defense.</b> This class hands back a stateless <see cref="IChatGate"/> that answers
/// one shift-question at a time — it has no memory of prior turns and cannot, by itself, decide when a
/// conversation has accumulated enough escalating turns to act. The stateful half — tracking a rolling window
/// of turns and arming quarantine after a threshold of flagged shifts — is
/// <c>AgentEval.MAF.Gatekeeper.Shadow.CrescendoTrajectoryJudge</c>, an <c>IShadowJudge</c> that wraps a gate
/// built by <see cref="Create"/> internally. Use THIS class only to calibrate the per-turn axis in isolation
/// (<see cref="CalibrateAsync"/>); use the <c>AgentEval.MAF</c> wrapper to actually run Crescendo detection
/// against a live multi-turn session.</para>
/// </summary>
public static class CrescendoTrajectoryTurnJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:crescendo-trajectory-turn-shift</c>).</summary>
    public const string Axis = "crescendo-trajectory-turn-shift";

    /// <summary>Builds the per-turn shift judge as an <see cref="IChatGate"/> over a fast model.</summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls.</param>
    /// <param name="options">Judge gate options. Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true).</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<CrescendoTrajectoryRubric>(new CrescendoTrajectoryRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>The canonical deterministic baseline this judge must beat — a naive keyword oracle.</summary>
    public static IChatGate KeywordBaseline() => new KeywordOracleGate(policyName: "keyword-oracle:crescendo-trajectory-turn-shift");

    /// <summary>The canonical both-directions gold set of per-turn-transition cases.</summary>
    public static JudgeGoldSet GoldSet() => CrescendoTrajectoryRubric.CalibrationGoldSet();

    /// <summary>
    /// Calibrate the per-turn shift judge against the canonical gold set and keyword-oracle baseline with a
    /// zero-missed-attacks bar. This calibrates the STATELESS core only — it does not exercise the
    /// stateful arm/no-arm behavior of the <c>AgentEval.MAF</c> wrapper, which has its own scripted
    /// integration-test suite for that.
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
