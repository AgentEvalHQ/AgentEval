// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;   // IEvaluableAgent

namespace AgentEval.RedTeam;

/// <summary>
/// Declared-then-trusted <see cref="SutTier"/> detection (Wave B, Pillar 1). <b>Floor, never ceiling</b>: classify
/// by the interfaces an agent implements and the capabilities it advertises; the runner runs each attack at the
/// highest <i>confirmed</i> tier and degrades honestly when the SUT cannot reach a higher one.
/// </summary>
public static class TierNegotiator
{
    /// <summary>Classifies the tier a SUT can be tested at, from the interfaces it implements and flags it advertises.</summary>
    public static SutTier Classify(IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent switch
        {
            IToolCapableAgent t when t.Capabilities.HasFlag(AgentToolCapability.InstrumentedTools) => SutTier.InstrumentedAgent,
            IToolCapableAgent t when t.Capabilities.HasFlag(AgentToolCapability.FunctionCalling) => SutTier.FunctionCalling,
            _ => SutTier.TextOnly,
        };
    }

    /// <summary>
    /// The fidelity a tool-call verdict earns at this tier. Note this is the <i>tier ceiling</i>; the evaluator
    /// still downgrades an <i>emitted-but-not-executed</i> call to <see cref="EvidenceFidelity.IntentToAct"/> even
    /// at <see cref="SutTier.InstrumentedAgent"/> (only an observed execution earns <see cref="EvidenceFidelity.Behavioral"/>).
    /// </summary>
    public static EvidenceFidelity FidelityFor(SutTier tier) => tier switch
    {
        SutTier.InstrumentedAgent => EvidenceFidelity.Behavioral,
        SutTier.FunctionCalling => EvidenceFidelity.IntentToAct,
        _ => EvidenceFidelity.Verbal,
    };
}
