// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/Benchmarks/RedTeamComplianceLeafTests.cs
//
// L17: the shared compliance-leaf builder must never fabricate a clean "pass" over zero conclusive evidence.
// The empty branch is unreachable from today's callers (they route empty → skipped upstream), so this direct test
// guards the conclusive-only discipline against a future caller that forgets the upstream skip-routing.
using AgentEval.Benchmarks;
using AgentEval.RedTeam;

namespace AgentEval.Tests.Benchmarks;

public class RedTeamComplianceLeafTests
{
    [Fact]
    public void BuildTestedLeaf_ZeroConclusive_ReturnsSkipped_NotFabricatedPass()
    {
        var probes = Enumerable.Range(0, 4)
            .Select(i => new ProbeResult
            {
                ProbeId = $"i{i}", Prompt = "p", Response = "r",
                Outcome = EvaluationOutcome.Inconclusive, Reason = "undecidable",
            })
            .ToList();
        var attack = new AttackResult
        {
            AttackName = "PromptInjection", OwaspId = "LLM01",
            ProbeResults = probes, InconclusiveCount = 4,
        };

        var leaf = RedTeamComplianceLeaf.BuildTestedLeaf(
            "owasp", "compliance.owasp", "LLM01", "Prompt Injection", "Prompt Injection",
            totalProbes: 4, passedProbes: 0, attacks: [attack]);

        Assert.Equal("skipped", leaf.Score.Label);
        Assert.False(leaf.Score.Passed);
        Assert.Equal(0.0, leaf.Score.Value);
    }
}
