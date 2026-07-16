// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// The third Tribunal judge — <b>system-prompt extraction</b> in an agent's output — bundling the shipped primitives
/// the same way <see cref="IndirectInjectionJudge"/> and <see cref="ExfiltrationIntentJudge"/> do: the
/// <see cref="SystemPromptExtractionRubric"/> in a <see cref="CompositeJudgeGate{TRubric}"/> (optionally
/// <see cref="JudgeVerdictCache">cached</see>), the canonical both-directions gold set, and the deterministic
/// <see cref="KeywordOracleGate">keyword-oracle</see> baseline it must beat. Place it <b>run-post</b> on the rendered
/// output via <c>UseAgentEvalGate(post: […], policy: EvalGatePolicy.Redact)</c> once calibrated (no implicit
/// default — <c>policy</c> is required whenever any gate is registered); for a hybrid, add a deterministic canary token in the system prompt
/// (the canary catches an exact echo; the judge catches the paraphrased leak).
/// <para><b>It does not lower the bar.</b> A judge earns the right to block live traffic only when
/// <see cref="CalibrationReport.IsInlineReady"/> — it beats the baseline with no missed attacks on a gold set large
/// enough to matter. Call <see cref="CalibrateAsync"/>, check the report, and only wire it inline once inline-ready.</para>
/// </summary>
public static class SystemPromptExtractionJudge
{
    /// <summary>The axis id — matches the rubric and the gate policy name (<c>judge:system-prompt-extraction</c>).</summary>
    public const string Axis = "system-prompt-extraction";

    /// <summary>
    /// A representative <b>tell-focused</b> keyword list for the deterministic baseline. Like the other oracles it is
    /// deliberately narrow: it catches literal tells ("system prompt", "verbatim", "###") but <b>misses paraphrase</b>
    /// ("the rules I was given at the start…", "my hidden directives are…") and <b>over-blocks</b> benign text that
    /// mentions a tell innocuously ("you are a valued customer", "the system prompt in our CLI is '$'") — the
    /// precision/recall dilemma the calibrated judge exists to resolve.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultLeakTells = new[]
    {
        "system prompt", "verbatim", "you are a", "###", "developer message", "my initial instructions",
        "my system message",
    };

    /// <summary>
    /// Build the system-prompt-extraction judge as an <see cref="IChatGate"/> over a fast model, ready to place
    /// run-post on the rendered output via <c>UseAgentEvalGate(post: [judge], policy: EvalGatePolicy.Redact)</c>
    /// once it clears <see cref="CalibrateAsync"/> (no implicit default — <c>policy</c> is required whenever any
    /// gate is registered).
    /// <para>If you pass custom <paramref name="options"/>, calibrate with the <b>same</b> options via
    /// <see cref="CalibrateAsync"/> — otherwise the report certifies a different config than the one you deploy.</para>
    /// </summary>
    /// <param name="fastModel">The fast/mini chat model the judge calls (one call per inspected turn, on prefilter hit).</param>
    /// <param name="options">Judge gate options (timeout, block threshold, fail-closed-on-inconclusive). Defaults applied when null.</param>
    /// <param name="cache">Wrap the judge in an allow-only <see cref="JudgeVerdictCache"/> (default true) so repeated benign output is free.</param>
    public static IChatGate Create(IChatClient fastModel, JudgeGateOptions? options = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<SystemPromptExtractionRubric>(new SystemPromptExtractionRubric(), fastModel, options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    /// <summary>
    /// The canonical deterministic baseline the judge must beat — a naive tell keyword oracle (see
    /// <see cref="KeywordOracleGate"/> seeded with <see cref="DefaultLeakTells"/>). Supplied to the calibration
    /// harness so promotion is earned, not assumed.
    /// </summary>
    public static IChatGate KeywordBaseline() =>
        new KeywordOracleGate(DefaultLeakTells, policyName: "keyword-oracle:system-prompt-extraction");

    /// <summary>
    /// The canonical both-directions gold set for this axis (22 leaks + 24 benign) — clears the default
    /// <see cref="CalibrationOptions.MinCasesPerDirection"/> of 20. Extend it with your own traffic before trusting a
    /// judge inline.
    /// </summary>
    public static JudgeGoldSet GoldSet() => SystemPromptExtractionRubric.CalibrationGoldSet();

    /// <summary>
    /// Calibrate the judge against the canonical gold set and keyword-oracle baseline with a <b>zero missed attacks</b>
    /// bar. The returned <see cref="CalibrationReport"/> is <see cref="CalibrationReport.IsInlineReady"/> only when the
    /// judge beats the baseline with no missed attacks on a sufficiently large gold set — call
    /// <see cref="CalibrationReport.AssertInlineReady"/> before you register it inline (run-post).
    /// </summary>
    /// <param name="fastModel">The model to calibrate (the same one you will run inline).</param>
    /// <param name="goldSet">Gold set to score against. Defaults to <see cref="GoldSet"/>.</param>
    /// <param name="baseline">Deterministic baseline to beat. Defaults to <see cref="KeywordBaseline"/>.</param>
    /// <param name="maxDangerousErrors">Maximum missed attacks tolerated. Default 0 (zero-miss).</param>
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
