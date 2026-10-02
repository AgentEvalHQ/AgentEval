// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.RedTeam.Evaluators;
using AgentEval.RedTeam.Gatekeeper;
using Microsoft.Agents.AI;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: Gatekeeper tool gates wrapped around an existing agent.</summary>
public static class GatekeeperSnippets
{
    public static AIAgent Protect(AIAgent baseAgent)
    {
        // begin-snippet: gatekeeper
        var agent = baseAgent.AsBuilder()
            .UseAgentEvalGate()   // per-run scope for the sequence gate
            .UseAgentEvalToolGate(
                [
                    // 🛑 Block DATA EXFILTRATION: reading customer data is fine, sending mail is fine —
                    //    the SEQUENCE is the attack. No tool-list trick catches this.
                    new SequenceGate(triggerTools: ["read_customer_data"], guardedTools: ["send_email", "http_post"]),

                    // 🎣 The SAME red-team oracle you test with, now a LIVE GUARD against a poisoned tool argument:
                    new ProbeEvaluatorGate(new ContainsTokenEvaluator("ignore previous instructions"), GateCost.PureCode),
                ],
                ToolGatePolicy.Terminate)   // block the call AND stop the loop
            .Build();
        // end-snippet
        return agent;
    }
}
