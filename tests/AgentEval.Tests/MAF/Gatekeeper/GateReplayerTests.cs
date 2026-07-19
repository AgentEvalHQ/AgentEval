// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Explainability &amp; Trust — counterfactual gate replay: does a candidate gate configuration produce a
/// DIFFERENT effective verdict than a baseline configuration, against the SAME captured tool calls, using the
/// real <see cref="IToolGate"/> objects (no simulation).
/// </summary>
public class GateReplayerTests
{
    private static GatedToolCall MakeCall(string functionName, IReadOnlyDictionary<string, object?>? args = null) =>
        new(functionName, args, AgentName: "TestAgent", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    private sealed class BlockByNameGate : IToolGate
    {
        private readonly string _target;
        public int CallCount { get; private set; }
        public string PolicyName => $"block-{_target}";
        public GateCost Cost => GateCost.PureCode;
        public BlockByNameGate(string target) => _target = target;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            CallCount++;
            return new(call.FunctionName == _target
                ? ToolGateVerdict.Block(PolicyName, $"{_target} is forbidden")
                : ToolGateVerdict.Allow(PolicyName));
        }
    }

    private sealed class AlwaysAllowGate : IToolGate
    {
        public int CallCount { get; private set; }
        public string PolicyName => "always-allow";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            CallCount++;
            return new(ToolGateVerdict.Allow(PolicyName));
        }
    }

    [Fact]
    public async Task NoGatesEitherSide_AllowsEverything_NoDivergence()
    {
        var calls = new[] { MakeCall("read_file"), MakeCall("send_email") };
        var result = await GateReplayer.CompareAsync(calls, baseline: [], candidate: []);

        Assert.All(result.Rows, r => Assert.Equal(ToolGateAction.Allow, r.Baseline.Action));
        Assert.All(result.Rows, r => Assert.Equal(ToolGateAction.Allow, r.Candidate.Action));
        Assert.Empty(result.Diverged);
    }

    [Fact]
    public async Task CandidateAddsBlockingGate_DivergesOnTheAffectedCall_NotOthers()
    {
        var calls = new[] { MakeCall("read_file"), MakeCall("send_email") };
        var candidateGate = new BlockByNameGate("send_email");

        var result = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [candidateGate]);

        var readRow = result.Rows.Single(r => r.Call.FunctionName == "read_file");
        var sendRow = result.Rows.Single(r => r.Call.FunctionName == "send_email");

        Assert.False(readRow.Diverged);
        Assert.True(sendRow.Diverged);
        Assert.Equal(ToolGateAction.Allow, sendRow.Baseline.Action);
        Assert.Equal(ToolGateAction.Block, sendRow.Candidate.Action);
        Assert.Single(result.Diverged);
        Assert.Equal("send_email", result.Diverged[0].Call.FunctionName);
    }

    [Fact]
    public async Task CandidateRelaxesBaselineBlock_DivergesTheOtherDirection()
    {
        var calls = new[] { MakeCall("delete_database") };
        var baselineGate = new BlockByNameGate("delete_database");

        var result = await GateReplayer.CompareAsync(calls, baseline: [baselineGate], candidate: []);

        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].Diverged);
        Assert.Equal(ToolGateAction.Block, result.Rows[0].Baseline.Action);
        Assert.Equal(ToolGateAction.Allow, result.Rows[0].Candidate.Action);
    }

    [Fact]
    public async Task IdenticalConfigsOnBothSides_NeverDiverge()
    {
        var calls = new[] { MakeCall("delete_database"), MakeCall("read_file") };
        var gateA = new BlockByNameGate("delete_database");
        var gateB = new BlockByNameGate("delete_database");   // separate instance, same rule — a fresh config, not a shared reference

        var result = await GateReplayer.CompareAsync(calls, baseline: [gateA], candidate: [gateB]);

        Assert.Empty(result.Diverged);
    }

    [Fact]
    public async Task SequentialEvaluation_FirstBlockWins_ShortCircuitsLaterGates()
    {
        var neverReached = new AlwaysAllowGate();
        var blocks = new BlockByNameGate("delete_database");
        var calls = new[] { MakeCall("delete_database") };

        // blocks runs first and is non-Allow, so neverReached must not be consulted — mirrors the live
        // AgentEvalToolGateExtensions foreach-until-non-Allow contract.
        var result = await GateReplayer.CompareAsync(calls, baseline: [blocks, neverReached], candidate: []);

        Assert.Equal(1, blocks.CallCount);
        Assert.Equal(0, neverReached.CallCount);
        Assert.Equal(ToolGateAction.Block, result.Rows[0].Baseline.Action);
    }

    [Fact]
    public async Task MultipleGates_AllAllow_NoneBlock_ResultIsAllow()
    {
        var gate1 = new AlwaysAllowGate();
        var gate2 = new AlwaysAllowGate();
        var calls = new[] { MakeCall("read_file") };

        var result = await GateReplayer.CompareAsync(calls, baseline: [gate1, gate2], candidate: [gate1, gate2]);

        Assert.Equal(2, gate1.CallCount);   // gate1 is the SAME instance in both lists — once per side evaluated == 2 calls total
        Assert.Equal(ToolGateAction.Allow, result.Rows[0].Baseline.Action);
        Assert.Equal(ToolGateAction.Allow, result.Rows[0].Candidate.Action);
        Assert.False(result.Rows[0].Diverged);
    }
}
