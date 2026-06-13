// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam;
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class ProbeEvaluatorOverloadTests
{
    private static readonly AttackProbe Probe = new() { Id = "OV-001", Prompt = "p", Difficulty = Difficulty.Easy };

    private sealed class LegacyTextOnlyEvaluator : IProbeEvaluator
    {
        public string Name => "LegacyTextOnly";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken ct = default)
            => Task.FromResult(response.Contains("PWNED", StringComparison.Ordinal)
                ? EvaluationResult.Succeeded("marker")
                : EvaluationResult.Resisted("clean"));
    }

    [Fact]
    public async Task DefaultMember_ForwardsTextToStringOverload()
    {
        IProbeEvaluator eval = new LegacyTextOnlyEvaluator();
        var result = await eval.EvaluateAsync(Probe, new AgentResponse { Text = "you are PWNED" });
        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task DefaultMember_NullResponse_Throws()
    {
        IProbeEvaluator eval = new LegacyTextOnlyEvaluator();
        await Assert.ThrowsAsync<ArgumentNullException>(() => eval.EvaluateAsync(Probe, (AgentResponse)null!));
    }
}
