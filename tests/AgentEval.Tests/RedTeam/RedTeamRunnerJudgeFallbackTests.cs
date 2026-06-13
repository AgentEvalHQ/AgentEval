// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.Testing; // FakeChatClient
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam;

public class RedTeamRunnerJudgeFallbackTests
{
    private sealed class AmbiguousAgent : IEvaluableAgent
    {
        public string Name => "Ambiguous";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = "hmm, maybe, it depends." });
    }

    private sealed class InconclusiveAttack : IAttackType
    {
        public string Name => "Inconclusive";
        public string DisplayName => "Inconclusive";
        public string Description => "test";
        public string OwaspLlmId => "LLM06";
        public string[] MitreAtlasIds => System.Array.Empty<string>();
        public Severity DefaultSeverity => Severity.High;
        public IReadOnlyList<AttackProbe> GetProbes(Intensity i) =>
            new[] { new AttackProbe { Id = "IC-1", Prompt = "p", Difficulty = Difficulty.Easy } };
        public IProbeEvaluator GetEvaluator() => new Inconclusive();
        private sealed class Inconclusive : IProbeEvaluator
        {
            public string Name => "Inconclusive";
            public Task<EvaluationResult> EvaluateAsync(AttackProbe p, string r, CancellationToken ct = default)
                => Task.FromResult(EvaluationResult.Inconclusive("cannot tell"));
        }
    }

    [Fact]
    public async Task InconclusiveDeterministic_WithJudgeClient_AdoptsJudgeVerdict()
    {
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.8\nREASON: Judge says compromised.");
        var options = new ScanOptions { AttackTypes = new[] { (IAttackType)new InconclusiveAttack() }, JudgeClient = judge };
        var result = await new RedTeamRunner().ScanAsync(new AmbiguousAgent(), options);
        var probe = result.AttackResults.Single().ProbeResults.Single();
        Assert.Equal(EvaluationOutcome.Succeeded, probe.Outcome);
        Assert.Equal(EvidenceFidelity.IntentToAct, probe.Fidelity);
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task InconclusiveDeterministic_NoJudgeClient_StaysInconclusive()
    {
        var options = new ScanOptions { AttackTypes = new[] { (IAttackType)new InconclusiveAttack() } };
        var result = await new RedTeamRunner().ScanAsync(new AmbiguousAgent(), options);
        Assert.Equal(EvaluationOutcome.Inconclusive, result.AttackResults.Single().ProbeResults.Single().Outcome);
    }
}
