// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// Thrown by <see cref="EvalGatingChatClient"/> under <see cref="EvalGatePolicy.ThrowOnFail"/> when a gate
/// blocks a turn. Mirrors the intent of the post-hoc <c>BehavioralPolicyViolationException</c>, applied inline.
/// </summary>
public sealed class EvalGateRefusalException : Exception
{
    /// <summary>The policy that refused the turn.</summary>
    public string PolicyName { get; }

    /// <summary>The gate action that triggered the refusal.</summary>
    public GateAction Action { get; }

    /// <summary>Which stage refused: "pre" (before the model call) or "post" (after).</summary>
    public string Stage { get; }

    /// <summary>Constructs the exception from a blocking <paramref name="verdict"/> at the given <paramref name="stage"/>.</summary>
    public EvalGateRefusalException(GateVerdict verdict, string stage)
        : base($"Gate '{verdict.PolicyName}' {verdict.Action} ({stage}): {verdict.Reason}")
    {
        PolicyName = verdict.PolicyName;
        Action = verdict.Action;
        Stage = stage;
    }
}
