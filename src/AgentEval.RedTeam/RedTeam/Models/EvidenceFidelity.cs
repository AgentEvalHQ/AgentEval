// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// RC-1: the kind of evidence a probe verdict rests on. Lets reports distinguish an agent that merely
/// *said* it would act from one that actually *invoked a tool*.
/// </summary>
public enum EvidenceFidelity
{
    /// <summary>Verdict derived only from the assistant's text (pattern/keyword/LLM-judge on text). Lowest fidelity.</summary>
    Verbal = 0,

    /// <summary>The text expresses concrete, imminent intent to act but the trace shows no tool call. Between Verbal and Behavioral.</summary>
    IntentToAct = 1,

    /// <summary>Verdict backed by an actual tool invocation observed in RawMessages. Highest fidelity.</summary>
    Behavioral = 2
}
