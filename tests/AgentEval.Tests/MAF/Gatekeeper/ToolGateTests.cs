// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Gatekeeper M1 — the full tool gate: policies (Throw/Terminate/Mutate), gates, cost rejection.</summary>
public class ToolGateTests
{
    private static (ChatClientAgent Agent, ScriptedChatClient Scripted) BuildAgent(AIFunction tool, string toolName, IDictionary<string, object?> args)
    {
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", toolName, args)
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [tool] },
        });
        return (agent, scripted);
    }

    // ── Policies ──

    [Fact]
    public async Task Terminate_Blocks_SetsTerminate_AndCountsAsBlock()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_file");
        var (agent, _) = BuildAgent(tool, "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_file")], ToolGatePolicy.Terminate, trace)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);   // Terminate still counts as a block
        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.ForbiddenToolGate"];
        Assert.Equal(true, value["terminate"]);
    }

    [Fact]
    public async Task MutateArgs_RewritesArgs_ToolReceivesMutatedValue()
    {
        // EMPIRICAL: does mutating context.Arguments reach the function? (Plan gates MutateArgs on this.)
        string? received = null;
        var tool = AIFunctionFactory.Create((string path) => { received = path; return path; }, "read_file");
        var (agent, _) = BuildAgent(tool, "read_file", new Dictionary<string, object?> { ["path"] = "/original" });
        var gate = new MutatingGate("read_file", new Dictionary<string, object?> { ["path"] = "/sandbox/original" });
        var gated = agent.AsBuilder().UseAgentEvalToolGate([gate]).Build();

        await gated.RunAsync("go");

        Assert.Equal("/sandbox/original", received);   // the tool ran with the MUTATED argument
    }

    // ── Build-time cost rejection ──

    [Fact]
    public void NetworkCostGate_RejectedAtConstruction()
    {
        var tool = AIFunctionFactory.Create((string path) => "ok", "x");
        var agent = new ChatClientAgent(new ScriptedChatClient().AddText("hi"), new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [tool] },
        });

        Assert.Throws<ArgumentException>(() =>
            agent.AsBuilder().UseAgentEvalToolGate([new NetworkCostGate()]).Build());
    }

    // ── Gates ──

    [Fact]
    public async Task ArgumentPatternGate_BlocksForbiddenArgument()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "ok"; }, "read_file");
        var (agent, _) = BuildAgent(tool, "read_file", new Dictionary<string, object?> { ["path"] = "/etc/shadow" });
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ArgumentPatternGate("/etc/shadow")], ToolGatePolicy.ReplaceResult)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);
    }

    [Fact]
    public void ArgumentPatternGate_UnboundedRegex_Throws()
        => Assert.Throws<ArgumentException>(() => new ArgumentPatternGate(new System.Text.RegularExpressions.Regex("x")));

    [Theory]
    [InlineData("<script", "<script>alert(1)</script>")]   // XSS metachars: default JSON escaping would hide these
    [InlineData("' OR", "admin' OR 1=1--")]                // SQLi
    [InlineData("&&", "ls && rm -rf /")]                    // command chaining
    public async Task ArgumentPatternGate_InjectionMetachars_AreNotEscapedAway_StillBlocks(string pattern, string payload)
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string input) => { Interlocked.Increment(ref executed); return "ok"; }, "render");
        var (agent, _) = BuildAgent(tool, "render", new Dictionary<string, object?> { ["input"] = payload });
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ArgumentPatternGate(pattern)], ToolGatePolicy.ReplaceResult)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);   // the injection payload was caught despite JSON metacharacter escaping
    }

    [Fact]
    public async Task ThrowingGate_FailsClosed_ToolDoesNotRun()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_file");
        var (agent, _) = BuildAgent(tool, "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ThrowingGate()], ToolGatePolicy.WarnOnly, trace)   // even WarnOnly must fail closed on a throw
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);   // a gate that throws blocks the tool (cannot-inspect => deny)
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);   // and it IS recorded as a block
    }

    [Fact]
    public async Task MutateThenBlock_BlockWins_TwoDistinctEvidenceKeys()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "ok"; }, "read_file");
        var (agent, _) = BuildAgent(tool, "read_file", new Dictionary<string, object?> { ["path"] = "/original" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate(
                [new MutatingGate("read_file", new Dictionary<string, object?> { ["path"] = "/sandbox" }), new ForbiddenToolGate("read_file")],
                ToolGatePolicy.ReplaceResult, trace)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);   // the second gate blocked
        Assert.Equal(2, (trace.Metadata!.Keys.Count(k => k.StartsWith("gate.tool.", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task MutateArgs_EvidenceRecordsValuesFaithfully_NotJsonEscaped()
    {
        // The before/after args in the Mutate audit must match what the tool actually receives — default JSON
        // escaping would render < > & as \uXXXX and make the audit misleading.
        var tool = AIFunctionFactory.Create((string html) => "ok", "render");
        var (agent, _) = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "x" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate(
                [new MutatingGate("render", new Dictionary<string, object?> { ["html"] = "a<b>&'c" })],
                ToolGatePolicy.WarnOnly, trace)
            .Build();

        await gated.RunAsync("go");

        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
        Assert.Contains("a<b>&'c", (string)value["argsAfter"]!);   // raw metacharacters, not < etc.
    }

    [Fact]
    public void UseAgentEvalToolGate_NullGateElement_ThrowsDescriptively()
    {
        var agent = new ChatClientAgent(new ScriptedChatClient().AddText("hi"), new ChatClientAgentOptions { Name = "T" });
        var ex = Assert.Throws<ArgumentException>(() =>
            agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("x"), null!], ToolGatePolicy.WarnOnly).Build());
        Assert.Equal("gates", ex.ParamName);
    }

    [Fact]
    public async Task SequenceGate_BlocksGuardedToolAfterTrigger()
    {
        var reads = 0;
        var sends = 0;
        var readTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref reads); return "secret"; }, "read_secrets");
        var sendTool = AIFunctionFactory.Create((string body) => { Interlocked.Increment(ref sends); return "sent"; }, "send_email");
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "read_secrets", new Dictionary<string, object?>())
            .AddToolCall("c2", "send_email", new Dictionary<string, object?> { ["body"] = "x" })
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [readTool, sendTool] },
        });
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new SequenceGate(["read_secrets"], ["send_email"])], ToolGatePolicy.ReplaceResult)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, reads);   // the trigger ran
        Assert.Equal(0, sends);   // but the guarded tool after it was blocked
    }

    // ── MCP coverage: a custom (non-factory) AIFunction subclass gates identically by name ──

    [Fact]
    public async Task McpShapedTool_IsGatedByName()
    {
        var mcpTool = new FakeMcpTool("delete_file");
        var (agent, _) = BuildAgent(mcpTool, "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" });
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_file")], ToolGatePolicy.ReplaceResult)
            .Build();

        await gated.RunAsync("go");

        Assert.False(mcpTool.WasInvoked);   // the MCP-shaped tool was blocked before execution
    }

    // ── Test doubles ──

    private sealed class MutatingGate : IToolGate
    {
        private readonly string _target;
        private readonly IReadOnlyDictionary<string, object?> _newArgs;
        public string PolicyName => "MutatingGate";
        public GateCost Cost => GateCost.PureCode;
        public MutatingGate(string target, IReadOnlyDictionary<string, object?> newArgs) { _target = target; _newArgs = newArgs; }
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(call.FunctionName == _target
                ? ToolGateVerdict.Mutate(PolicyName, _newArgs, "sandboxed")
                : ToolGateVerdict.Allow(PolicyName));
    }

    private sealed class NetworkCostGate : IToolGate
    {
        public string PolicyName => "NetworkCostGate";
        public GateCost Cost => GateCost.Network;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(ToolGateVerdict.Allow(PolicyName));
    }

    private sealed class ThrowingGate : IToolGate
    {
        public string PolicyName => "ThrowingGate";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class FakeMcpTool : AIFunction
    {
        public bool WasInvoked { get; private set; }
        public override string Name { get; }
        public FakeMcpTool(string name) => Name = name;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            return new ValueTask<object?>("done");
        }
    }
}
