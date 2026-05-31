// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam;
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam;

public class RedTeamRunnerFidelityTests
{
    [Fact]
    public async Task ScanAsync_ToolAwareEvaluator_PropagatesBehavioralFidelity()
    {
        var agent = new ToolInvokingFakeAgent("admin_delete");
        var options = new ScanOptions { AttackTypes = new[] { (IAttackType)new ToolAwareAttack("admin_delete") } };

        var result = await new RedTeamRunner().ScanAsync(agent, options);

        var probe = result.AttackResults.Single().ProbeResults.Single();
        Assert.Equal(EvaluationOutcome.Succeeded, probe.Outcome);
        // Behavioral fidelity comes from the evaluator's explicit Metadata["fidelity"], not inference.
        Assert.Equal(EvidenceFidelity.Behavioral, probe.Fidelity);
    }

    [Fact]
    public async Task ScanAsync_TextOnlyEvaluator_WithIncidentalToolCall_StaysVerbal()
    {
        // A string-only evaluator returns Succeeded from text; an unrelated tool call in the trace
        // must NOT upgrade fidelity to Behavioral (RC-1 correctness).
        var agent = new ToolInvokingFakeAgent("search");
        var options = new ScanOptions { AttackTypes = new[] { (IAttackType)new AlwaysSucceedsAttack() } };

        var result = await new RedTeamRunner().ScanAsync(agent, options);

        var probe = result.AttackResults.Single().ProbeResults.Single();
        Assert.Equal(EvaluationOutcome.Succeeded, probe.Outcome);
        Assert.Equal(EvidenceFidelity.Verbal, probe.Fidelity);
    }

    private sealed class ToolInvokingFakeAgent : IEvaluableAgent
    {
        private readonly string _tool;
        public ToolInvokingFakeAgent(string tool) => _tool = tool;
        public string Name => "ToolInvokingFake";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            var id = $"call-{_tool}";
            var raw = new List<object>
            {
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    new List<Microsoft.Extensions.AI.AIContent>
                    { new Microsoft.Extensions.AI.FunctionCallContent(id, _tool, null) })
            };
            return Task.FromResult(new AgentResponse { Text = "ok", RawMessages = raw });
        }
    }

    private sealed class ToolAwareAttack : IAttackType
    {
        private readonly string _forbidden;
        public ToolAwareAttack(string forbidden) => _forbidden = forbidden;
        public string Name => "ToolAware";
        public string DisplayName => "Tool Aware";
        public string Description => "test";
        public string OwaspLlmId => "LLM06";
        public string[] MitreAtlasIds => Array.Empty<string>();
        public Severity DefaultSeverity => Severity.High;
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) =>
            new[] { new AttackProbe { Id = "TA-1", Prompt = "do it", Difficulty = Difficulty.Easy } };
        public IProbeEvaluator GetEvaluator() =>
            new AgentEval.RedTeam.Evaluators.ToolInvocationEvaluator(new[] { _forbidden });
    }

    private sealed class AlwaysSucceedsAttack : IAttackType
    {
        public string Name => "AlwaysSucceeds";
        public string DisplayName => "Always Succeeds";
        public string Description => "test";
        public string OwaspLlmId => "LLM06";
        public string[] MitreAtlasIds => Array.Empty<string>();
        public Severity DefaultSeverity => Severity.High;
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) =>
            new[] { new AttackProbe { Id = "AS-1", Prompt = "do it", Difficulty = Difficulty.Easy } };
        public IProbeEvaluator GetEvaluator() => new AlwaysSucceedsEvaluator();
        private sealed class AlwaysSucceedsEvaluator : IProbeEvaluator
        {
            public string Name => "AlwaysSucceeds";
            public Task<EvaluationResult> EvaluateAsync(AttackProbe p, string r, CancellationToken ct = default)
                => Task.FromResult(EvaluationResult.Succeeded("always"));
        }
    }
}
