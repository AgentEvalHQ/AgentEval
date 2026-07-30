// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Defines directional gates for content crossing an inter-agent boundary. The inbound half screens content
/// returned by another agent and reuses <see cref="IndirectInjectionJudge"/>. The outbound half compares a
/// delegated instruction with a separately supplied trusted parent goal before transport.
/// </summary>
/// <remarks>
/// Place the gate returned by <see cref="CreateInbound"/> at the remote agent's run-post seam. For a MAF
/// <c>AIAgent</c>, add it to <c>GatekeeperOptions.PostGates</c> while wrapping that remote agent. Remote tools
/// execute server-side and are not intercepted by this chat-content gate.
/// Use <c>CreateOutbound</c> as a run-pre gate; its trusted goal must come from application/session state,
/// never from the outbound instruction being inspected.
/// <para>
/// Reusing the canonical rubric does not promote a model automatically. Calibrate the exact model, options,
/// and deployment traffic with <see cref="CalibrateInboundAsync"/>, and require
/// <see cref="CalibrationReport.IsInlineReady"/> before enforcing it inline.
/// </para>
/// <para>
/// Enforced run-post inspection requires a buffered, non-streaming response. A streaming caller cannot retract
/// chunks that were already delivered; Gatekeeper therefore rejects enforced streaming before remote transport.
/// Use observe-only streaming or buffer the remote response at a trusted boundary when streaming is required.
/// </para>
/// </remarks>
public static partial class InterAgentBoundaryInjectionGate
{
    /// <summary>
    /// The inbound axis id. It intentionally matches <see cref="IndirectInjectionJudge.Axis"/> because this is
    /// the same semantic detector applied at the inter-agent response boundary.
    /// </summary>
    public const string InboundAxis = IndirectInjectionJudge.Axis;

    /// <summary>Builds the inbound response gate over a fast judge model.</summary>
    /// <param name="fastModel">The model used to judge boundary content.</param>
    /// <param name="options">Judge timeout, threshold, and inconclusive-result behavior.</param>
    /// <param name="cache">Whether repeated benign content may use the allow-only verdict cache.</param>
    /// <returns>An inbound <see cref="IChatGate"/> for the remote agent's run-post seam.</returns>
    public static IChatGate CreateInbound(
        IChatClient fastModel,
        JudgeGateOptions? options = null,
        bool cache = true)
        => IndirectInjectionJudge.Create(fastModel, options, cache);

    /// <summary>Returns the deterministic baseline the inbound judge must beat.</summary>
    public static IChatGate InboundKeywordBaseline() => IndirectInjectionJudge.KeywordBaseline();

    /// <summary>
    /// Returns the canonical indirect-injection gold set. Deployments should extend it with representative
    /// inter-agent responses before trusting a materially different traffic distribution.
    /// </summary>
    public static JudgeGoldSet InboundGoldSet() => IndirectInjectionJudge.GoldSet();

    /// <summary>
    /// Calibrates the inbound gate against its gold set and deterministic baseline.
    /// </summary>
    /// <param name="fastModel">The same judge model that will be deployed inline.</param>
    /// <param name="goldSet">Boundary-response cases to score; defaults to <see cref="InboundGoldSet"/>.</param>
    /// <param name="baseline">Baseline to beat; defaults to <see cref="InboundKeywordBaseline"/>.</param>
    /// <param name="maxDangerousErrors">Maximum missed attacks; defaults to zero.</param>
    /// <param name="options">The same judge options that will be passed to <see cref="CreateInbound"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The calibration report used to decide whether inline enforcement is allowed.</returns>
    public static Task<CalibrationReport> CalibrateInboundAsync(
        IChatClient fastModel,
        JudgeGoldSet? goldSet = null,
        IChatGate? baseline = null,
        int maxDangerousErrors = 0,
        JudgeGateOptions? options = null,
        CancellationToken cancellationToken = default)
        => IndirectInjectionJudge.CalibrateAsync(
            fastModel,
            goldSet ?? InboundGoldSet(),
            baseline,
            maxDangerousErrors,
            options,
            cancellationToken);
}
