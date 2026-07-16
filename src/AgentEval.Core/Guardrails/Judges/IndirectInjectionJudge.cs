// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// The flagship Tribunal judge for <b>indirect prompt injection</b> — a one-call bundling of the shipped primitives:
/// the <see cref="IndirectInjectionRubric"/> wrapped in a <see cref="CompositeJudgeGate{TRubric}"/> (optionally
/// <see cref="JudgeVerdictCache">cached</see>), the canonical both-directions gold set, and the deterministic
/// <see cref="KeywordOracleGate">keyword-oracle</see> baseline it must beat.
/// <para><b>It does not lower the bar.</b> Calibration still decides: a judge earns the right to block live traffic
/// only when <see cref="CalibrationReport.IsInlineReady"/> — it beats the baseline with no missed attacks on a gold
/// set that is large enough. Call <see cref="CalibrateAsync"/>, check the report, and only wire it inline (run-pre on
/// the tool/RAG-return seam) once it is inline-ready.</para>
/// </summary>
public static class IndirectInjectionJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:indirect-injection</c>).</summary>
    public const string Axis = "indirect-injection";

    /// <summary>
    /// Build the indirect-injection judge as an <see cref="IChatGate"/> over a fast model, ready to place run-pre on
    /// the tool/RAG-return seam via <c>UseAgentEvalGate(pre: [judge], policy: EvalGatePolicy.Redact)</c> once it
    /// clears <see cref="CalibrateAsync"/> (no implicit default — <c>policy</c> is required whenever any gate is
    /// registered).
    /// <para>If you pass custom <paramref name="options"/> (e.g. a different timeout, or a fail-open inconclusive
    /// policy), calibrate with the <b>same</b> options via <see cref="CalibrateAsync"/> — otherwise the report
    /// certifies a different config than the one you deploy.</para>
    /// </summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls (one call per inspected turn, on prefilter hit).</param>
    /// <param name="options">Judge gate options (timeout, block threshold, fail-closed-on-inconclusive). Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true) so repeated benign content is free.</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<IndirectInjectionRubric>(new IndirectInjectionRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>
    /// The canonical deterministic baseline the judge must beat — a naive keyword oracle (see
    /// <see cref="KeywordOracleGate"/>). Supplied to the calibration harness so promotion is earned, not assumed.
    /// </summary>
    public static IChatGate KeywordBaseline() => new KeywordOracleGate(policyName: "keyword-oracle:indirect-injection");

    /// <summary>
    /// The canonical both-directions gold set for this axis (26 attacks + 26 benign) — clears the default
    /// <see cref="CalibrationOptions.MinCasesPerDirection"/> of 20. Extend it with your own traffic before trusting a
    /// judge inline.
    /// </summary>
    public static JudgeGoldSet GoldSet() => IndirectInjectionRubric.CalibrationGoldSet();

    /// <summary>
    /// Calibrate the judge against the canonical gold set and keyword-oracle baseline with a <b>zero missed attacks</b>
    /// bar. The returned <see cref="CalibrationReport"/> is <see cref="CalibrationReport.IsInlineReady"/> only when the
    /// judge beats the baseline with no missed attacks on a sufficiently large gold set — call
    /// <see cref="CalibrationReport.AssertInlineReady"/> before you register it inline.
    /// </summary>
    /// <param name="fastModel">The model to calibrate (the same one you will run inline).</param>
    /// <param name="goldSet">Gold set to score against. Defaults to <see cref="GoldSet"/>.</param>
    /// <param name="baseline">Deterministic baseline to beat. Defaults to <see cref="KeywordBaseline"/>.</param>
    /// <param name="maxDangerousErrors">Maximum missed attacks tolerated. Default 0 (zero-miss).</param>
    /// <param name="options">
    /// Judge gate options to calibrate. Pass the <b>same</b> options you deploy via <see cref="Create"/> — otherwise
    /// the report certifies a different config than the one that runs inline (e.g. calibrating fail-closed but
    /// deploying <see cref="JudgeGateOptions.FailClosedOnInconclusive"/>=false, which fails open on abstention).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task<CalibrationReport> CalibrateAsync(
        IChatClient fastModel,
        JudgeGoldSet? goldSet = null,
        IChatGate? baseline = null,
        int maxDangerousErrors = 0,
        JudgeGateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Calibrate the raw judge (no cache) — every gold case is distinct, and we want to measure the model itself.
        // (The cache is allow-only and can never flip a verdict, so cache:false here still certifies a cache:true deploy.)
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
