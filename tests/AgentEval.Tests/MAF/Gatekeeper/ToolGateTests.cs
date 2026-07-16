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
        var gated = agent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.WarnOnly).Build();

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
            agent.AsBuilder().UseAgentEvalToolGate([new NetworkCostGate()], ToolGatePolicy.WarnOnly).Build());
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
        // escaping would render < > & as \uXXXX and make the audit misleading. Only TraceCaptureMode.Full
        // records raw values at all (the new default, Redacted, never writes a value into the trace — see
        // MutateArgs_DefaultCaptureMode_IsRedacted_NeverWritesValue below) — opt into Full explicitly here.
        var tool = AIFunctionFactory.Create((string html) => "ok", "render");
        var (agent, _) = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "x" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate(
                [new MutatingGate("render", new Dictionary<string, object?> { ["html"] = "a<b>&'c" })],
                ToolGatePolicy.WarnOnly, trace, mutationCaptureMode: TraceCaptureMode.Full)
            .Build();

        await gated.RunAsync("go");

        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
        Assert.Contains("a<b>&'c", (string)value["argsAfter"]!);   // raw metacharacters, not < etc.
    }

    [Fact]
    public async Task MutateArgs_DefaultCaptureMode_IsRedacted_NeverWritesValue()
    {
        // #13: Redacted is the new default — the actual argument VALUE must never appear in the trace, even
        // though the mutation (which keys changed) is still auditable.
        var tool = AIFunctionFactory.Create((string html) => "ok", "render");
        var (agent, _) = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "x" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate(
                [new MutatingGate("render", new Dictionary<string, object?> { ["html"] = "TOP-SECRET-VALUE" })],
                ToolGatePolicy.WarnOnly, trace)   // no mutationCaptureMode passed — must default to Redacted
            .Build();

        await gated.RunAsync("go");

        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
        Assert.DoesNotContain("TOP-SECRET-VALUE", (string)value["argsAfter"]!);
        Assert.Contains("html", (string)value["argsAfter"]!);   // the key IS still visible — only the value is redacted
        Assert.Equal(nameof(TraceCaptureMode.Redacted), value["captureMode"]);
    }

    [Fact]
    public void UseAgentEvalToolGate_NullGateElement_ThrowsDescriptively()
    {
        var agent = new ChatClientAgent(new ScriptedChatClient().AddText("hi"), new ChatClientAgentOptions { Name = "T" });
        var ex = Assert.Throws<ArgumentException>(() =>
            agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("x"), null!], ToolGatePolicy.WarnOnly).Build());
        Assert.Equal("gates", ex.ParamName);
    }

    // ── #4-revisit: best-effort RUNTIME missing-run-scope signal (registration-time cannot detect this) ──

    [Fact]
    public async Task RunScopeGate_NoScopeEstablished_RecordsOneWarning_OnFirstCallOnly()
    {
        var reads = 0;
        var readTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref reads); return "ok"; }, "read_data");
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "read_data", new Dictionary<string, object?>())
            .AddToolCall("c2", "read_data", new Dictionary<string, object?>())
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [readTool] } });
        var trace = new AgentTrace();

        // No UseAgentEvalGate() — RunBudgetGate declares GateRequirements.RunScope but no scope is ever established.
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new RunBudgetGate(maxToolCalls: 10)], ToolGatePolicy.WarnOnly, trace)
            .Build();

        await gated.RunAsync("go");   // 2 tool calls happen in this one run

        Assert.Equal(2, reads);   // WarnOnly never blocks — this is advisory, not enforcement
        var warnings = trace.Metadata!.Keys.Where(k => k.Contains("MissingRunScope", StringComparison.Ordinal)).ToList();
        Assert.Single(warnings);   // recorded once, not once per call
        var value = (IDictionary<string, object?>)trace.Metadata![warnings[0]];
        Assert.Equal("Warn", value["action"]);
        Assert.Contains("RunBudgetGate", (string)value["reason"]!, StringComparison.Ordinal);
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace).GateBlockCount);   // never counted as a block
    }

    [Fact]
    public async Task RunScopeGate_ScopeEstablished_NoWarningRecorded()
    {
        var readTool = AIFunctionFactory.Create(() => "ok", "read_data");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "read_data", new Dictionary<string, object?>()).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [readTool] } });
        var trace = new AgentTrace();

        var gated = agent.AsBuilder()
            .UseAgentEvalGate(trace: trace)   // establishes the scope
            .UseAgentEvalToolGate([new RunBudgetGate(maxToolCalls: 10)], ToolGatePolicy.WarnOnly, trace)
            .Build();

        await gated.RunAsync("go");

        // No warning ⇒ nothing was ever recorded at all here (RunBudgetGate under WarnOnly never blocks either).
        Assert.True(trace.Metadata is null || !trace.Metadata.Keys.Any(k => k.Contains("MissingRunScope", StringComparison.Ordinal)));
    }

    // ── Chaining TWO SEPARATE UseAgentEvalToolGate registrations inverts the intuitive order ──

    [Fact]
    public async Task ChainedRegistrations_LaterCallsGatesRunFirst_AndCanStarveTheEarlierCall()
    {
        // AgentEvalToolGateExtensions.UseAgentEvalToolGate is built on MAF's
        // FunctionInvocationDelegatingAgentBuilderExtensions.Use(AIAgentBuilder, callback) — a middleware-chain
        // seam. Empirically (not assumed): registering TWO SEPARATE UseAgentEvalToolGate calls on the same
        // builder makes the SECOND (later) registration the OUTERMOST layer, so its gates see the call FIRST —
        // the opposite of what "register A then B" reads as. If the later registration's gate blocks (without
        // calling next), the earlier registration's gate is never even invoked. This is why UseAgentEvalToolGate
        // is documented to require ONE call with ALL gates in ONE list (or UseGatekeeper, which already does
        // that) — chaining silently inverts precedence.
        var firstCalled = 0;
        var secondCalled = 0;
        var firstGate = new RecordingGate("First", () => Interlocked.Increment(ref firstCalled));
        var secondGate = new RecordingGate("Second", () => Interlocked.Increment(ref secondCalled));

        var executed = 0;
        var tool = AIFunctionFactory.Create((string x) => { Interlocked.Increment(ref executed); return "ok"; }, "shared_tool");
        var (agent, _) = BuildAgent(tool, "shared_tool", new Dictionary<string, object?> { ["x"] = "1" });

        // Registered in order: first, then second — a reader's natural assumption is "first's gate runs first."
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([firstGate], ToolGatePolicy.Terminate)
            .UseAgentEvalToolGate([secondGate], ToolGatePolicy.Terminate)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);       // the call was blocked (by one of the two — Terminate either way)
        Assert.Equal(1, secondCalled);   // the LATER registration's gate WAS invoked
        Assert.Equal(0, firstCalled);    // the EARLIER registration's gate was NEVER invoked — starved, not just "ran second"
    }

    private sealed class RecordingGate : IToolGate
    {
        private readonly Action _onCalled;
        public string PolicyName { get; }
        public GateCost Cost => GateCost.PureCode;

        public RecordingGate(string policyName, Action onCalled)
        {
            PolicyName = policyName;
            _onCalled = onCalled;
        }

        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            _onCalled();
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName, "always blocks — proves whether InspectAsync ran at all"));
        }
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

    // ── Gatekeeper Hardening Phase 2 fixture: parallel tool calls (multiple FunctionCallContent in one turn) ──

    [Fact]
    public async Task ParallelToolCalls_EachCallGatedIndependently_OneBlockedOneAllowed()
    {
        // Proves the fixture actually exercises MAF's FunctionInvokingChatClient invoking N calls from a
        // SINGLE assistant turn (not N separate scripted turns) — a gate must inspect and decide each of the
        // N calls on its own, in the same FICC iteration (FunctionCount > 1).
        var reads = 0;
        var sends = 0;
        var readTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref reads); return "ok"; }, "read_data");
        var sendTool = AIFunctionFactory.Create((string body) => { Interlocked.Increment(ref sends); return "sent"; }, "send_email");
        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "read_data", new Dictionary<string, object?>()),
                ("c2", "send_email", new Dictionary<string, object?> { ["body"] = "x" }))
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [readTool, sendTool] },
        });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("send_email")], ToolGatePolicy.ReplaceResult, trace)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, reads);   // read_data was allowed and actually ran
        Assert.Equal(0, sends);   // send_email, in the SAME turn, was independently blocked
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
    }

    [Fact]
    public async Task ParallelToolCalls_BothCallsAllowed_BothExecute()
    {
        var callCount = 0;
        var tool = AIFunctionFactory.Create((string city) => { Interlocked.Increment(ref callCount); return $"weather for {city}"; }, "weather");
        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "weather", new Dictionary<string, object?> { ["city"] = "tokyo" }),
                ("c2", "weather", new Dictionary<string, object?> { ["city"] = "osaka" }))
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new AllowAllToolGate()], ToolGatePolicy.WarnOnly)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(2, callCount);   // both parallel calls actually reached the tool function
    }

    private sealed class AllowAllToolGate : IToolGate
    {
        public string PolicyName => "AllowAllToolGate";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(ToolGateVerdict.Allow(PolicyName));
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
