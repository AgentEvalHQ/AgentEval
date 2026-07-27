// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

public static partial class InterAgentBoundaryInjectionGate
{
    /// <summary>The outbound delegated-instruction goal-drift axis.</summary>
    public const string OutboundAxis = InterAgentOutboundGoalDriftRubric.AxisName;

    /// <summary>Maximum trusted-parent-goal characters accepted by the outbound formatter.</summary>
    public const int MaxTrustedGoalChars = 8_000;

    /// <summary>Maximum outbound delegated-instruction characters accepted by the outbound formatter.</summary>
    public const int MaxOutboundInstructionChars = 16_000;

    /// <summary>
    /// Builds an outbound run-pre gate bound to one immutable trusted parent goal.
    /// </summary>
    /// <param name="fastModel">The model used to judge goal drift.</param>
    /// <param name="trustedParentGoal">Trusted goal supplied out of band, never inferred from the instruction.</param>
    /// <param name="options">Judge timeout, threshold, bounds, and inconclusive-result behavior.</param>
    /// <param name="cache">Whether repeated allowed goal/instruction pairs may use the allow-only cache.</param>
    /// <returns>An outbound gate for the remote agent's run-pre seam.</returns>
    public static IChatGate CreateOutbound(
        IChatClient fastModel,
        string trustedParentGoal,
        JudgeGateOptions? options = null,
        bool cache = true)
    {
        ValidatePart(
            trustedParentGoal,
            MaxTrustedGoalChars,
            nameof(trustedParentGoal),
            "trusted parent goal");
        return CreateOutbound(
            fastModel,
            _ => ValueTask.FromResult<string?>(trustedParentGoal),
            options,
            cache);
    }

    /// <summary>
    /// Builds an outbound run-pre gate that resolves trusted parent context for each inspected turn.
    /// </summary>
    /// <param name="fastModel">The model used to judge goal drift.</param>
    /// <param name="trustedGoalResolver">
    /// Per-turn resolver backed by trusted application/session state. It must not derive the goal from the outbound
    /// instruction being inspected. Missing, invalid, or failed context blocks before the model or remote call.
    /// </param>
    /// <param name="options">Judge timeout, threshold, bounds, and inconclusive-result behavior.</param>
    /// <param name="cache">Whether repeated allowed goal/instruction pairs may use the allow-only cache.</param>
    /// <returns>An outbound gate for the remote agent's run-pre seam.</returns>
    public static IChatGate CreateOutbound(
        IChatClient fastModel,
        Func<CancellationToken, ValueTask<string?>> trustedGoalResolver,
        JudgeGateOptions? options = null,
        bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        ArgumentNullException.ThrowIfNull(trustedGoalResolver);
        var judge = CreateOutboundJudge(fastModel, options, cache);
        var maxFormattedChars = options?.MaxInputChars ?? new JudgeGateOptions().MaxInputChars;
        return new OutboundTrustedGoalGate(
            trustedGoalResolver,
            judge,
            maxFormattedChars);
    }

    /// <summary>Formats a trusted goal and outbound instruction for the outbound rubric.</summary>
    /// <exception cref="ArgumentException">Either value is blank or exceeds its fixed safety bound.</exception>
    public static string FormatOutboundCase(
        string trustedParentGoal,
        string outboundInstruction)
    {
        ValidatePart(
            trustedParentGoal,
            MaxTrustedGoalChars,
            nameof(trustedParentGoal),
            "trusted parent goal");
        ValidatePart(
            outboundInstruction,
            MaxOutboundInstructionChars,
            nameof(outboundInstruction),
            "outbound instruction");
        return InterAgentOutboundGoalDriftRubric.FormatCase(
            trustedParentGoal,
            outboundInstruction);
    }

    /// <summary>Returns the deterministic baseline the outbound judge must beat.</summary>
    public static IChatGate OutboundKeywordBaseline() =>
        new KeywordOracleGate(policyName: $"keyword-oracle:{OutboundAxis}");

    /// <summary>Returns the canonical 24/24 outbound goal-drift calibration corpus.</summary>
    public static JudgeGoldSet OutboundGoldSet() =>
        InterAgentOutboundGoalDriftRubric.CalibrationGoldSet();

    /// <summary>Calibrates the outbound judge against its corpus and deterministic baseline.</summary>
    /// <param name="fastModel">The same judge model that will be deployed inline.</param>
    /// <param name="goldSet">Outbound cases to score; defaults to <see cref="OutboundGoldSet"/>.</param>
    /// <param name="baseline">Baseline to beat; defaults to <see cref="OutboundKeywordBaseline"/>.</param>
    /// <param name="maxDangerousErrors">Maximum missed hijacks; defaults to zero.</param>
    /// <param name="options">The same judge options that will be passed to <c>CreateOutbound</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The calibration report used to decide whether inline enforcement is allowed.</returns>
    public static Task<CalibrationReport> CalibrateOutboundAsync(
        IChatClient fastModel,
        JudgeGoldSet? goldSet = null,
        IChatGate? baseline = null,
        int maxDangerousErrors = 0,
        JudgeGateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var judge = CreateOutboundJudge(fastModel, options, cache: false);
        return GateCalibrationHarness.EvaluateAsync(
            judge,
            goldSet ?? OutboundGoldSet(),
            new CalibrationOptions
            {
                DeterministicBaseline = baseline ?? OutboundKeywordBaseline(),
                MaxDangerousErrors = maxDangerousErrors,
            },
            cancellationToken);
    }

    private static IChatGate CreateOutboundJudge(
        IChatClient fastModel,
        JudgeGateOptions? options,
        bool cache)
    {
        ArgumentNullException.ThrowIfNull(fastModel);
        IChatGate gate = new CompositeJudgeGate<InterAgentOutboundGoalDriftRubric>(
            new InterAgentOutboundGoalDriftRubric(),
            fastModel,
            options);
        return cache ? new JudgeVerdictCache(gate) : gate;
    }

    private static void ValidatePart(
        string? value,
        int maxChars,
        string parameterName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} must be non-empty.", parameterName);
        }

        if (value.Length > maxChars)
        {
            throw new ArgumentException(
                $"{description} exceeds the {maxChars}-character safety bound.",
                parameterName);
        }
    }

    private sealed class OutboundTrustedGoalGate(
        Func<CancellationToken, ValueTask<string?>> trustedGoalResolver,
        IChatGate inner,
        int maxFormattedChars) : IChatGate
    {
        public string PolicyName => inner.PolicyName;

        public async ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text) ||
                text.Length > MaxOutboundInstructionChars)
            {
                return ContextFailure("outbound instruction is missing or exceeds its safety bound");
            }

            string? trustedGoal;
            try
            {
                trustedGoal = await trustedGoalResolver(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return ContextFailure("trusted parent goal resolver failed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(trustedGoal) ||
                trustedGoal.Length > MaxTrustedGoalChars)
            {
                return ContextFailure("trusted parent goal is missing or exceeds its safety bound");
            }

            var formatted = InterAgentOutboundGoalDriftRubric.FormatCase(
                trustedGoal,
                text);
            if (maxFormattedChars > 0 && formatted.Length > maxFormattedChars)
            {
                return ContextFailure("formatted boundary case exceeds the judge input bound");
            }

            return await inner.InspectAsync(formatted, cancellationToken).ConfigureAwait(false);
        }

        private GateVerdict ContextFailure(string reason) =>
            GateVerdict.Block(PolicyName, $"{OutboundAxis}: {reason} (fail-closed)");
    }
}
