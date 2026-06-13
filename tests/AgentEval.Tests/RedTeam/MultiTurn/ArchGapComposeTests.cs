// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Architecture-gap LOW fixes:
//   Area 2 — AttackPipeline can now set Judge/Attacker/multi-turn options (PAIR/TAP no longer error via the pipeline).
//   Area 4 — a multi-turn attack that is ALSO IToolAwareAttack routes canary tools over the conversation channel.
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Tests.TestHelpers;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.RedTeam.MultiTurn;

public class ArchGapComposeTests
{
    // ───────────────────────── shared doubles ─────────────────────────

    /// <summary>Plain tool-capable agent (NOT IConversableAgent → driven via StatelessConversationAdapter). Records
    /// which channel (tool-aware vs text-only) the orchestrator drove it through.</summary>
    private sealed class RecordingToolAgent : IEvaluableAgent, IToolCapableAgent
    {
        public List<IReadOnlyList<CanaryTool>> ToolCalls { get; } = [];
        public int PlainCalls { get; private set; }
        public string Name => "recording-tool";
        public AgentToolCapability Capabilities => AgentToolCapability.FunctionCalling | AgentToolCapability.InstrumentedTools;

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            PlainCalls++;
            return Task.FromResult(new AgentResponse { Text = "text-only reply" });
        }

        public Task<AgentResponse> InvokeWithToolsAsync(string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken ct = default)
        {
            ToolCalls.Add(tools);
            return Task.FromResult(new AgentResponse { Text = "tool channel reply" });
        }
    }

    private static AttackProbe Seed => new() { Id = "S", Prompt = "p", Difficulty = Difficulty.Hard };

    private abstract class BaseMultiTurnAttack : IAttackType, IMultiTurnAttack
    {
        public abstract string Name { get; }
        public string DisplayName => Name;
        public string Description => "test";
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.High;
        public IProbeEvaluator GetEvaluator() => new ContainsTokenEvaluator("ZZ-NEVER");
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [Seed];
        public int MaxTurns => 1;
        public abstract Task<string?> NextTurnAsync(MultiTurnContext context, CancellationToken cancellationToken = default);
        public IConvergenceDetector ConvergenceDetector => SuccessOnlyConvergenceDetector.Instance;
    }

    /// <summary>Multi-turn AND tool-aware: advertises one canary tool and sends a single user message.</summary>
    private sealed class ToolAwareMultiTurnAttack(CanaryTool tool) : BaseMultiTurnAttack, IToolAwareAttack
    {
        public override string Name => "ToolAwareMT";
        public override Task<string?> NextTurnAsync(MultiTurnContext c, CancellationToken ct = default)
            => Task.FromResult<string?>(c.TurnIndex == 0 ? "do the thing" : null);
        public IReadOnlyList<CanaryTool> GetCanaryTools(Intensity intensity) => [tool];
    }

    /// <summary>Multi-turn but NOT tool-aware.</summary>
    private sealed class PlainMultiTurnAttack : BaseMultiTurnAttack
    {
        public override string Name => "PlainMT";
        public override Task<string?> NextTurnAsync(MultiTurnContext c, CancellationToken ct = default)
            => Task.FromResult<string?>(c.TurnIndex == 0 ? "do the thing" : null);
    }

    /// <summary>Captures the AttackerClient the orchestrator threads from ScanOptions (Wave C′), then exhausts.</summary>
    private sealed class AttackerCapturingAttack : BaseMultiTurnAttack
    {
        public IChatClient? CapturedAttackerClient { get; private set; }
        public bool Invoked { get; private set; }
        public override string Name => "AttackerCapturing";
        public override Task<string?> NextTurnAsync(MultiTurnContext c, CancellationToken ct = default)
        {
            Invoked = true;
            CapturedAttackerClient = c.AttackerClient;
            return Task.FromResult<string?>(null);   // give up immediately — we only need the captured client
        }
    }

    private sealed class AlwaysReplyAgent(string reply) : IEvaluableAgent
    {
        public string Name => "always-reply";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = reply });
    }

    // ───────────────────────── Area 4: multi-turn ↔ tool harness ─────────────────────────

    [Fact]
    public async Task ToolAwareMultiTurnAttack_RoutesCanaryToolsOverConversationChannel()
    {
        var agent = new RecordingToolAgent();
        var tool = new CanaryTool { Name = "exfiltrate", Description = "send data out", ForbiddenCategory = "DataExfiltration" };
        var attack = new ToolAwareMultiTurnAttack(tool);

        await new TurnOrchestrator(agent, new ScanOptions { Intensity = Intensity.Quick })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.Single(agent.ToolCalls);                         // the tool-aware SendAsync overload was used
        Assert.Equal("exfiltrate", agent.ToolCalls[0].Single().Name);
        Assert.Equal(0, agent.PlainCalls);                      // NOT the text-only path
    }

    [Fact]
    public async Task NonToolAwareMultiTurnAttack_UsesTextOnlyPath()
    {
        var agent = new RecordingToolAgent();
        var attack = new PlainMultiTurnAttack();

        await new TurnOrchestrator(agent, new ScanOptions { Intensity = Intensity.Quick })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.Empty(agent.ToolCalls);                          // no tools advertised → tool channel untouched
        Assert.Equal(1, agent.PlainCalls);                      // plain text-only InvokeAsync used
    }

    // ───────────────────────── Area 2: pipeline can set Judge/Attacker/multi-turn ─────────────────────────

    [Fact]
    public void WithJudge_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => AttackPipeline.Create().WithJudge(null!));

    [Fact]
    public void WithAttacker_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => AttackPipeline.Create().WithAttacker(null!));

    [Fact]
    public async Task Pipeline_WithAttacker_PropagatesAttackerClientToMultiTurnAttack()
    {
        IChatClient attacker = new MockStreamingChatClient([], "unused");
        var attack = new AttackerCapturingAttack();

        await AttackPipeline.Create()
            .WithAttack(attack)
            .WithAttacker(attacker)
            .WithIntensity(Intensity.Quick)
            .ScanAsync(new AlwaysReplyAgent("nope"));

        Assert.True(attack.Invoked);
        Assert.Same(attacker, attack.CapturedAttackerClient);   // the pipeline now wires ScanOptions.AttackerClient
    }

    [Fact]
    public async Task Pipeline_WithoutAttacker_LeavesAttackerClientNull()
    {
        var attack = new AttackerCapturingAttack();

        await AttackPipeline.Create()
            .WithAttack(attack)
            .WithIntensity(Intensity.Quick)
            .ScanAsync(new AlwaysReplyAgent("nope"));

        Assert.True(attack.Invoked);
        Assert.Null(attack.CapturedAttackerClient);
    }

    [Fact]
    public void MultiTurnBuilderMethods_AreChainable()
    {
        var pipeline = AttackPipeline.Create()
            .WithTimeoutPerTurn(TimeSpan.FromSeconds(5))
            .WithMaxConversationDuration(TimeSpan.FromSeconds(30))
            .WithParallelism(3);
        Assert.NotNull(pipeline);   // fluent chain returns the pipeline
    }
}
