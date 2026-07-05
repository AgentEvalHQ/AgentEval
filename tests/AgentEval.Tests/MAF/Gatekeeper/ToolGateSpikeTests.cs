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

/// <summary>
/// Gatekeeper M0 — the seam spike. Proves the MAF function-invocation middleware blocks a live tool call,
/// records evidence the shipped Glass Box reader counts, and fails closed on a non-FICC agent.
/// </summary>
public class ToolGateSpikeTests
{
    [Fact]
    public async Task ForbiddenTool_ReplaceResult_BlocksLiveCall_AndRecordsEvidence()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "deleted"; }, "delete_file");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [tool] },
        });
        var trace = new AgentTrace();

        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_file")], ToolGatePolicy.ReplaceResult, trace)
            .Build();

        await gated.RunAsync("go");

        // (a) the tool delegate never executed
        Assert.Equal(0, executed);

        // (b) the refusal is surfaced to the model — as a FunctionResultContent (NOT a TextContent, so .Text won't see it)
        Assert.True(scripted.ReceivedMessages.Any(list =>
            list.Any(m => m.Contents.OfType<FunctionResultContent>()
                .Any(r => r.Result?.ToString()?.Contains("BLOCKED") == true))));

        // (c) evidence recorded under the shipped gate.* key shape
        Assert.True(trace.Metadata!.ContainsKey("gate.tool.1.ForbiddenToolGate"));

        // (d) the shipped Glass Box evidence reader counts it with zero read-path changes
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
    }

    [Fact]
    public async Task WarnOnly_RecordsEvidence_ButLetsToolRun()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "deleted"; }, "delete_file");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [tool] },
        });
        var trace = new AgentTrace();

        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_file")], ToolGatePolicy.WarnOnly, trace)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, executed); // WarnOnly: the tool still ran
        // Honest evidence: a WarnOnly finding is recorded as "Warn", NOT counted as a block (the tool ran).
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.ForbiddenToolGate"];
        Assert.Equal("Warn", value["action"]);
    }

    [Fact]
    public async Task NonFicc_FailsClosed_ThrowsAtBuildOrRun()
    {
        // A ChatClientAgent that uses the raw ScriptedChatClient AS-IS inserts no FunctionInvokingChatClient,
        // so the tool-gate middleware has no FICC to attach to and MUST fail closed.
        var scripted = new ScriptedChatClient().AddText("hi");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            UseProvidedChatClientAsIs = true,
        });

        AIAgent? built = null;
        var ex = Record.Exception(() => built = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("x")])
            .Build());

        if (ex is null && built is not null)
        {
            ex = await Record.ExceptionAsync(() => built.RunAsync("go"));
        }

        Assert.NotNull(ex); // fail-closed: throws at Build() OR at first RunAsync, never "neither"
    }
}
