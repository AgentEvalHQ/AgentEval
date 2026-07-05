// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M4) — the arming CONSUMER: a fail-closed run-pre session gate that blocks a run whose
/// session was quarantined by a <see cref="ShadowJudgePump"/> verdict (an earlier run was judged compromised).
/// Closes the shadow loop: expensive async judge → arm <c>StateBag</c> flag → this gate refuses to resume the
/// compromised conversation on the next run. Register as a <c>pre</c> gate on <c>UseAgentEvalGate</c>.
/// </summary>
public sealed class QuarantineGate : SessionContextGate
{
    /// <inheritdoc/>
    public override string PolicyName => "QuarantineGate";

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var quarantined =
            session.StateBag.TryGetValue<string>(ShadowJudgePump.QuarantineStateKey, out var reason, JsonSerializerOptions.Default)
            && !string.IsNullOrEmpty(reason);

        return new ValueTask<GateVerdict>(quarantined
            ? GateVerdict.Block(PolicyName, $"session quarantined by a shadow-judge verdict ({reason}) — refusing to resume a compromised conversation")
            : GateVerdict.Allow(PolicyName));
    }
}
