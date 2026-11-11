// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Prompts;

public sealed partial class LocalizedNarrativeTests
{
    private const string Narrative = """{"explanation":"The response contains the planted marker."}""";
    private static AttackProbe Probe() => new() { Id = "explain", Prompt = "Emit MARKER", Technique = "marker", Difficulty = Difficulty.Easy };

    [Fact]
    public async Task Explain_ConfiguredSelection_UsesSeparateNarrativeContract()
    {
        using var client = new FakeChatClient(Narrative);
        var judge = new LLMJudgeEvaluator(client, new() { JudgePrompts = new() });
        var text = await judge.ExplainAsync(Probe(), "MARKER", EvaluationOutcome.Succeeded, EvidenceFidelity.Verbal);
        Assert.Equal("The response contains the planted marker.", text);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Scan_ConfiguredNarrative_DoesNotChangeEstablishedVerdict()
    {
        using var client = new FakeChatClient(Narrative);
        var result = await new RedTeamRunner().ScanAsync(new Subject(), new()
        { AttackTypes = [new MarkerAttack()], JudgeClient = client, JudgePrompts = new(), ExplainFindings = true });
        var probe = Assert.Single(Assert.Single(result.AttackResults).ProbeResults);
        Assert.Equal(EvaluationOutcome.Succeeded, probe.Outcome);
        Assert.Equal("The response contains the planted marker.", probe.Rationale);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Scan_CallerCancelledDuringLegacyNarration_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var client = new CancelClient(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RedTeamRunner().ScanAsync(new Subject(), new()
        { AttackTypes = [new MarkerAttack()], JudgeClient = client, ExplainFindings = true }, cts.Token));
    }

    private sealed class Subject(string response = "MARKER") : IEvaluableAgent
    {
        public string Name => "Narrative fixture";
        public int Calls;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AgentResponse { Text = response });
        }
    }

    private class MarkerAttack : IAttackType
    {
        public string Name => "Marker";
        public string DisplayName => Name;
        public string Description => Name;
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Low;
        public IProbeEvaluator GetEvaluator() => new ContainsTokenEvaluator(StringComparison.Ordinal, "MARKER");
        public virtual IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [Probe()];
    }

    private sealed class CancelClient(CancellationTokenSource source) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
