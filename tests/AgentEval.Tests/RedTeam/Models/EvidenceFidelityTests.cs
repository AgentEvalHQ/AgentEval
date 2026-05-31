// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam.Models;

public class EvidenceFidelityTests
{
    [Fact]
    public void ProbeResult_DefaultsToVerbalFidelity()
    {
        var pr = new ProbeResult { ProbeId = "X", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Resisted, Reason = "ok" };
        Assert.Equal(EvidenceFidelity.Verbal, pr.Fidelity);
    }

    [Fact]
    public void Fidelity_Ordering_BehavioralIsHighest()
    {
        Assert.True(EvidenceFidelity.Behavioral > EvidenceFidelity.IntentToAct);
        Assert.True(EvidenceFidelity.IntentToAct > EvidenceFidelity.Verbal);
    }
}
