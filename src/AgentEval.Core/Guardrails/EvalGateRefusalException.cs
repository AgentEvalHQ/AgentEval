// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// Thrown by <see cref="EvalGatingChatClient"/> under <see cref="EvalGatePolicy.ThrowOnFail"/> when a gate
/// blocks a turn. Mirrors the intent of the post-hoc <c>BehavioralPolicyViolationException</c>, applied inline.
/// <para><b><see cref="Exception.Message"/> is non-revealing</b> (Phase 1, #7/#12-follow-up) — it does NOT
/// interpolate the policy name or reason. This mirrors the same reasoning behind <c>GateReferenceId</c>'s
/// model-visible refusal shape in <c>AgentEval.MAF.Gatekeeper</c>, applied here too: <c>Core</c> cannot depend
/// on <c>MAF</c> (the opposite reference direction), so this exception could not simply reuse that type's fix —
/// but the SAME risk applies. <c>ThrowOnFail</c> is documented as "app code, not model-visible content," which
/// is true for the direct case (a caller catches this around <c>agent.RunAsync(...)</c> in their own code) —
/// but an agent-as-tool / sub-agent boundary that naively turns a caught exception's <c>.Message</c> into a
/// tool-result string would leak it to a model anyway, exactly like the refusal text <c>GateReferenceId</c> was
/// built to stop leaking. Use <see cref="PolicyName"/>/<see cref="Reason"/>/<see cref="ReferenceId"/> — the full
/// structured detail, safe for genuinely app-only consumption — instead of parsing <see cref="Exception.Message"/>.</para>
/// </summary>
public sealed class EvalGateRefusalException : Exception
{
    /// <summary>The policy that refused the turn.</summary>
    public string PolicyName { get; }

    /// <summary>The gate action that triggered the refusal.</summary>
    public GateAction Action { get; }

    /// <summary>Which stage refused: "pre" (before the model call) or "post" (after).</summary>
    public string Stage { get; }

    /// <summary>The blocking gate's own reason — structured, non-<see cref="Exception.Message"/> access to the full detail.</summary>
    public string? Reason { get; }

    /// <summary>A short, opaque, unpredictable id correlating this exception with any trace evidence recorded for the same block.</summary>
    public string ReferenceId { get; }

    /// <summary>Constructs the exception from a blocking <paramref name="verdict"/> at the given <paramref name="stage"/>.</summary>
    public EvalGateRefusalException(GateVerdict verdict, string stage)
        : base(BuildMessage(stage, out var referenceId))
    {
        PolicyName = verdict.PolicyName;
        Action = verdict.Action;
        Stage = stage;
        Reason = verdict.Reason;
        ReferenceId = referenceId;
    }

    private static string BuildMessage(string stage, out string referenceId)
    {
        referenceId = GateCorrelationId.New();
        return $"Gate blocked the '{stage}' stage (referenceId: {referenceId}). See the .PolicyName/.Reason " +
               "properties for full detail — this message is intentionally non-revealing in case it crosses " +
               "into model-visible territory (e.g. an agent-as-tool boundary that surfaces a caught exception's .Message).";
    }
}
