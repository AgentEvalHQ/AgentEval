// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// The <b>utility valve</b> judge — <b>over-refusal</b> in an agent's output — bundling the shipped primitives like
/// the security judges, but with an inverted purpose: it protects <i>usefulness</i>. With several fail-closed judges
/// live, the fleet trends toward block-everything; this one flags the wrongful refusals so they don't pass silently.
/// Bundles the <see cref="OverRefusalRubric"/> in a <see cref="CompositeJudgeGate{TRubric}"/>, the canonical gold set,
/// and the naive refusal-detector baseline it must beat.
/// <para><b>Advisory — wire it <see cref="EvalGatePolicy.WarnOnly">WarnOnly</see>.</b> A positive verdict is a
/// <i>flag</i> (recorded as a <c>gate.run-post.*.judge:over-refusal</c> warning for an offline false-refusal metric or
/// a retry path), never an enforced block — hard-blocking a refusal is nonsensical and would punish honesty, the
/// opposite of the point. Calibration still applies: the flag is only trustworthy once the judge beats the baseline
/// on a gold set.</para>
/// </summary>
public static class OverRefusalJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:over-refusal</c>).</summary>
    public const string Axis = "over-refusal";

    /// <summary>
    /// A representative naive <b>refusal-detector</b> keyword list for the deterministic baseline — it flags any
    /// refusal marker. Like the security oracles it is deliberately narrow, and here that produces the classic
    /// dilemma: it <b>false-alarms</b> on JUSTIFIED refusals ("I can't help create malware") and non-refusal uses of
    /// "can't"/"sorry", and it <b>misses</b> marker-less soft over-refusals ("I'd rather not…"). A calibrated judge
    /// beats it by reading intent, not surface markers.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultRefusalMarkers = new[]
    {
        "i can't", "i cannot", "i'm sorry", "unable to", "i won't", "cannot assist", "can't help", "must decline",
        "not able to",
    };

    /// <summary>
    /// Build the over-refusal judge as an <see cref="IChatGate"/> over a fast model. Place it <b>run-post</b> under
    /// <see cref="EvalGatePolicy.WarnOnly"/> via <c>UseAgentEvalGate(post: [judge], policy: WarnOnly)</c> — it records
    /// a warning flag and lets the answer through; it must not enforce.
    /// <para>If you pass custom <paramref name="options"/>, calibrate with the <b>same</b> options via
    /// <see cref="CalibrateAsync"/>.</para>
    /// </summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls (one call per inspected turn, on prefilter hit).</param>
    /// <param name="options">Judge gate options (timeout, block threshold, fail-closed-on-inconclusive). Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true) so repeated benign output is free.</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<OverRefusalRubric>(new OverRefusalRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>
    /// The canonical deterministic baseline the judge must beat — a naive refusal-marker oracle (see
    /// <see cref="KeywordOracleGate"/> seeded with <see cref="DefaultRefusalMarkers"/>). Supplied to the calibration
    /// harness so promotion is earned, not assumed.
    /// </summary>
    public static IChatGate KeywordBaseline() =>
        new KeywordOracleGate(DefaultRefusalMarkers, policyName: "keyword-oracle:over-refusal");

    /// <summary>
    /// The canonical both-directions gold set for this axis (22 over-refusals + 24 non-over-refusals) — clears the
    /// default <see cref="CalibrationOptions.MinCasesPerDirection"/> of 20. Extend it with your own traffic.
    /// </summary>
    public static JudgeGoldSet GoldSet() => OverRefusalRubric.CalibrationGoldSet();

    /// <summary>
    /// Calibrate the judge against the canonical gold set and refusal-marker baseline with a <b>zero missed cases</b>
    /// bar (here a "missed case" is a missed over-refusal flag). Only trust the flag once
    /// <see cref="CalibrationReport.IsInlineReady"/>.
    /// </summary>
    /// <param name="fastModel">The model to calibrate (the same one you will run inline).</param>
    /// <param name="goldSet">Gold set to score against. Defaults to <see cref="GoldSet"/>.</param>
    /// <param name="baseline">Deterministic baseline to beat. Defaults to <see cref="KeywordBaseline"/>.</param>
    /// <param name="maxDangerousErrors">Maximum missed over-refusals tolerated. Default 0.</param>
    /// <param name="options">Judge gate options to calibrate. Pass the <b>same</b> options you deploy via <see cref="Create"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
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
