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
using AgentEval.Testing;   // Turn
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
        // Declared on the base (not via the interface DIM) so subclasses can override and the override is dispatched
        // through IMultiTurnAttack — a derived class member alone would NOT re-map the base's DIM implementation.
        public virtual bool UsesAttacker => false;
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
        public override bool UsesAttacker => true;   // Jun14-M13: this double simulates an attacker-driven attack
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

    /// <summary>L9: an agent that is BOTH IConversableAgent AND IToolCapableAgent, whose native conversation does NOT
    /// override the tool DIM (so the native channel would silently drop canary tools). Records its tool-channel calls.</summary>
    private sealed class ConversableToolAgent : IEvaluableAgent, IConversableAgent, IToolCapableAgent
    {
        public List<IReadOnlyList<CanaryTool>> ToolCalls { get; } = [];
        public string Name => "conversable-tool";
        public AgentToolCapability Capabilities => AgentToolCapability.FunctionCalling | AgentToolCapability.InstrumentedTools;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = "text reply" });
        public Task<AgentResponse> InvokeWithToolsAsync(string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken ct = default)
        {
            ToolCalls.Add(tools);
            return Task.FromResult(new AgentResponse { Text = "tool channel reply" });
        }
        public Task<IAgentConversation> StartConversationAsync(CancellationToken ct = default)
            => Task.FromResult<IAgentConversation>(new Convo());

        private sealed class Convo : IAgentConversation   // native channel; tool DIM left at default (drops tools)
        {
            private readonly List<Turn> _history = [];
            public ConversationFidelity Fidelity => ConversationFidelity.Native;
            public IReadOnlyList<Turn> History => _history;
            public Task<AgentResponse> SendAsync(string userMessage, CancellationToken ct = default)
            {
                _history.Add(Turn.User(userMessage));
                _history.Add(Turn.Assistant("native text reply"));
                return Task.FromResult(new AgentResponse { Text = "native text reply" });
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>L12: a native conversation that DOES carry tools (CarriesTools=true, overrides the tool DIM) — must
    /// keep Native fidelity rather than being forced to the flattened adapter.</summary>
    private sealed class ToolCarryingConversableAgent : IEvaluableAgent, IConversableAgent, IToolCapableAgent
    {
        public string Name => "tool-carrying-convo";
        public AgentToolCapability Capabilities => AgentToolCapability.FunctionCalling | AgentToolCapability.InstrumentedTools;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = "text" });
        public Task<AgentResponse> InvokeWithToolsAsync(string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = "should not be reached" });
        public Task<IAgentConversation> StartConversationAsync(CancellationToken ct = default)
            => Task.FromResult<IAgentConversation>(new Convo());

        public List<IReadOnlyList<CanaryTool>> ToolSends { get; } = [];
        private sealed class Convo : IAgentConversation
        {
            private readonly List<Turn> _history = [];
            public ConversationFidelity Fidelity => ConversationFidelity.Native;
            public bool CarriesTools => true;   // this native channel routes tools itself
            public IReadOnlyList<Turn> History => _history;
            public Task<AgentResponse> SendAsync(string userMessage, CancellationToken ct = default)
                => Task.FromResult(new AgentResponse { Text = "native text reply" });
            public Task<AgentResponse> SendAsync(string userMessage, IReadOnlyList<CanaryTool> tools, CancellationToken ct = default)
                => Task.FromResult(new AgentResponse { Text = "native tool-aware reply" });
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    [Fact] // L9: a native conversable+tool-capable agent must not silently drop canary tools — fall back to the
    // flattened adapter that bridges to InvokeWithToolsAsync (honest Flattened fidelity, not a dropped Tier-2 boundary).
    public async Task NativeConversableThatIsAlsoToolCapable_DoesNotSilentlyDropCanaryTools()
    {
        var agent = new ConversableToolAgent();
        var tool = new CanaryTool { Name = "exfiltrate", Description = "send data out", ForbiddenCategory = "DataExfiltration" };
        var attack = new ToolAwareMultiTurnAttack(tool);

        var result = await new TurnOrchestrator(agent, new ScanOptions { Intensity = Intensity.Quick })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.Single(agent.ToolCalls);                                          // the tool boundary WAS exercised
        Assert.Equal("exfiltrate", agent.ToolCalls[0].Single().Name);
        Assert.Equal(ConversationFidelity.Flattened, result.ConversationFidelity); // honest downgrade from Native
    }

    [Fact] // Jun14-L12: a native conversation that DOES carry tools (CarriesTools=true) keeps Native fidelity — the
    // L9 fallback only fires when the native channel genuinely can't carry them.
    public async Task NativeConversableThatCarriesTools_KeepsNativeFidelity()
    {
        var agent = new ToolCarryingConversableAgent();
        var tool = new CanaryTool { Name = "exfiltrate", Description = "send data out", ForbiddenCategory = "DataExfiltration" };
        var attack = new ToolAwareMultiTurnAttack(tool);

        var result = await new TurnOrchestrator(agent, new ScanOptions { Intensity = Intensity.Quick })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.Equal(ConversationFidelity.Native, result.ConversationFidelity);   // not forced to Flattened
    }

    [Fact] // L10: an attacker LLM generating turns marks the folded result non-deterministic.
    public async Task TurnOrchestrator_AttackerClientSet_FoldsAttackerDrivenTrue()
    {
        IChatClient attacker = new MockStreamingChatClient([], "unused");
        var attack = new AttackerCapturingAttack();

        var result = await new TurnOrchestrator(new AlwaysReplyAgent("nope"),
                new ScanOptions { Intensity = Intensity.Quick, AttackerClient = attacker })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.True(result.AttackerDriven);
    }

    [Fact]
    public async Task TurnOrchestrator_NoAttackerClient_FoldsAttackerDrivenFalse()
    {
        var attack = new AttackerCapturingAttack();

        var result = await new TurnOrchestrator(new AlwaysReplyAgent("nope"),
                new ScanOptions { Intensity = Intensity.Quick })
            .RunAsync(attack, Seed, attack.GetEvaluator(), default);

        Assert.False(result.AttackerDriven);
    }

    [Fact] // Jun14-M13: a SCRIPTED multi-turn attack (UsesAttacker=false) is NOT marked attacker-driven even when
    // --attacker is supplied scan-wide — otherwise a baseline gate could suppress a real regression on it.
    public async Task TurnOrchestrator_ScriptedAttack_WithAttackerClient_FoldsAttackerDrivenFalse()
    {
        IChatClient attacker = new MockStreamingChatClient([], "unused");
        var scriptedAttack = new PlainMultiTurnAttack();   // UsesAttacker defaults to false

        var result = await new TurnOrchestrator(new AlwaysReplyAgent("nope"),
                new ScanOptions { Intensity = Intensity.Quick, AttackerClient = attacker })
            .RunAsync(scriptedAttack, Seed, scriptedAttack.GetEvaluator(), default);

        Assert.False(result.AttackerDriven);
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
