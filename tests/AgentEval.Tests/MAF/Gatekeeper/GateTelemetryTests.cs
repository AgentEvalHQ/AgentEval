// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Gatekeeper Phase 1, #18 — gate effectiveness telemetry.</summary>
public class GateTelemetryTests
{
    private static ChatClientAgent BuildAgent(AIFunction tool, string toolName, IDictionary<string, object?> args)
    {
        var scripted = new ScriptedChatClient().AddToolCall("call_1", toolName, args).AddText("done");
        return new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
    }

    [Fact]
    public async Task Allow_RecordsInvocationAndAllowCount()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var agent = BuildAgent(tool, "lookup_order", new Dictionary<string, object?> { ["x"] = "1" });
        var telemetry = new GateTelemetry();
        var gated = agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate, telemetry: telemetry).Build();

        await gated.RunAsync("go");

        var snapshot = Assert.Single(telemetry.Snapshot());
        Assert.Equal("ForbiddenToolGate", snapshot.PolicyName);
        Assert.Equal(1, snapshot.InvocationCount);
        Assert.Equal(1, snapshot.AllowCount);
        Assert.Equal(0, snapshot.BlockCount);
        Assert.Equal(0, snapshot.MutateCount);
    }

    [Fact]
    public async Task Block_RecordsBlockCount_RegardlessOfPolicy()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var telemetry = new GateTelemetry();
        var gated = agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.WarnOnly, telemetry: telemetry).Build();

        await gated.RunAsync("go");

        var snapshot = Assert.Single(telemetry.Snapshot());
        Assert.Equal(1, snapshot.BlockCount);   // recorded even though WarnOnly let the tool run
    }

    [Fact]
    public async Task ThrowingGate_RecordsAsBlock()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var telemetry = new GateTelemetry();
        var gated = agent.AsBuilder().UseAgentEvalToolGate([new ThrowingGate()], ToolGatePolicy.WarnOnly, telemetry: telemetry).Build();

        await gated.RunAsync("go");

        var snapshot = Assert.Single(telemetry.Snapshot());
        Assert.Equal("ThrowingGate", snapshot.PolicyName);
        Assert.Equal(1, snapshot.BlockCount);
    }

    [Fact]
    public async Task MultipleInvocations_AccumulateAcrossCalls()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var telemetry = new GateTelemetry();
        var gate = new ForbiddenToolGate("delete_account");

        for (var i = 0; i < 3; i++)
        {
            var agent = BuildAgent(tool, "lookup_order", new Dictionary<string, object?> { ["x"] = i.ToString() });
            var gated = agent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.Terminate, telemetry: telemetry).Build();
            await gated.RunAsync("go");
        }

        var snapshot = Assert.Single(telemetry.Snapshot());
        Assert.Equal(3, snapshot.InvocationCount);
        Assert.Equal(3, snapshot.AllowCount);
    }

    [Fact]
    public async Task MultipleGates_EachGetsItsOwnSnapshot()
    {
        // Distinct PolicyNames (ForbiddenToolGate's PolicyName is fixed per-type, not per-instance) so telemetry
        // keys them separately — pair it with ArgumentPatternGate, which has a different PolicyName.
        var tool = AIFunctionFactory.Create((string x) => x, "lookup_order");
        var agent = BuildAgent(tool, "lookup_order", new Dictionary<string, object?> { ["x"] = "1" });
        var telemetry = new GateTelemetry();
        var gated = agent.AsBuilder()
            .UseAgentEvalToolGate([new ForbiddenToolGate("a"), new ArgumentPatternGate("never-matches-anything-xyz")], ToolGatePolicy.Terminate, telemetry: telemetry)
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(2, telemetry.Snapshot().Count);
    }

    [Fact]
    public void Reset_ClearsCounters()
    {
        var telemetry = new GateTelemetry();
        telemetry.Record("X", ToolGateAction.Allow, TimeSpan.FromMilliseconds(1));
        Assert.NotEmpty(telemetry.Snapshot());

        telemetry.Reset();

        Assert.Empty(telemetry.Snapshot());
    }

    [Fact]
    public void NoTelemetryPassed_NoThrow_GateStillWorks()
    {
        // Absence of a telemetry sink must be a complete no-op, not a null-ref anywhere on the hot path.
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var gated = agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate).Build();

        var ex = Record.Exception(() => gated.RunAsync("go").GetAwaiter().GetResult());
        Assert.Null(ex);
    }

    private sealed class ThrowingGate : IToolGate
    {
        public string PolicyName => "ThrowingGate";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
