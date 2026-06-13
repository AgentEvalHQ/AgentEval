// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// The observable layer a system-under-test (SUT) exposes to the harness — a <b>floor, never a ceiling</b>
/// (Wave B, Pillar 1). The runner runs each tool-aware attack at the highest <i>confirmed</i> tier and labels the
/// verdict's <see cref="EvidenceFidelity"/> accordingly, degrading honestly when the SUT cannot reach a higher tier.
/// </summary>
public enum SutTier
{
    /// <summary>Text-only endpoint; only a refusal/compliance text heuristic is possible. Maps to <see cref="EvidenceFidelity.Verbal"/>.</summary>
    TextOnly = 0,

    /// <summary>
    /// Function-calling endpoint: it emits <c>tool_call</c>s that we harvest WITHOUT executing them. An emitted
    /// forbidden call is intent-to-act, not effect. Maps to <see cref="EvidenceFidelity.IntentToAct"/>.
    /// </summary>
    FunctionCalling = 1,

    /// <summary>
    /// An instrumented agent we control: canary tools actually run and record execution, so a forbidden call is an
    /// observed effect. Maps to <see cref="EvidenceFidelity.Behavioral"/>.
    /// </summary>
    InstrumentedAgent = 2,
}
