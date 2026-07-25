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

    /// <summary>Rewrites every call's arguments to a fixed replacement (a "normalizer" stand-in).</summary>
    private sealed class RewriteArgsGate : IToolGate
    {
        private readonly IReadOnlyDictionary<string, object?> _newArgs;
        public int CallCount { get; private set; }
        public string PolicyName => "rewrite-args";
        public GateCost Cost => GateCost.PureCode;
        public RewriteArgsGate(IReadOnlyDictionary<string, object?> newArgs) => _newArgs = newArgs;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            CallCount++;
            return new(ToolGateVerdict.Mutate(PolicyName, _newArgs, "rewritten"));
        }
    }

    /// <summary>Blocks when any argument VALUE contains <c>needle</c> — used to prove a later gate sees mutated args.</summary>
    private sealed class BlockIfArgContainsGate : IToolGate
    {
        private readonly string _needle;
        public int CallCount { get; private set; }
        public string PolicyName => $"block-arg-contains:{_needle}";
        public GateCost Cost => GateCost.PureCode;
        public BlockIfArgContainsGate(string needle) => _needle = needle;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            CallCount++;
            var hit = call.Arguments?.Values.Any(v => v is string s && s.Contains(_needle, StringComparison.Ordinal)) ?? false;
            return new(hit ? ToolGateVerdict.Block(PolicyName, $"argument contains '{_needle}'") : ToolGateVerdict.Allow(PolicyName));
        }
    }

    private sealed class ThrowingGate : IToolGate
    {
        public int CallCount { get; private set; }
        public string PolicyName => "throwing-gate";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("gate blew up");
        }
    }

    // P2-4: rewrites the arguments to a DIFFERENT value every inspection, so they never converge.
    private sealed class EverChangingRewriteGate : IToolGate
    {
        private int _n;
        public string PolicyName => "ever-changing";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(ToolGateVerdict.Mutate(PolicyName, new Dictionary<string, object?> { ["path"] = (++_n).ToString() }, "churn"));
    }

    [Fact]
    public async Task Mutate_DoesNotShortCircuit_LaterGateSeesMutatedArgs_AndCanBlock()
    {
        // P2-4b / WM-4: a "normalize path" mutator rewrites args to a traversal; a later-ordered gate that blocks
        // on ".." MUST see the mutated value and block — the replayer no longer short-circuits on Mutate (which
        // would have hidden this exact escalation, diverging from the live loop where the tool would be blocked).
        var normalize = new RewriteArgsGate(new Dictionary<string, object?> { ["path"] = "../../etc/passwd" });
        var traversalBlocker = new BlockIfArgContainsGate("..");
        var calls = new[] { MakeCall("read_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }) };

        var result = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [normalize, traversalBlocker]);

        Assert.Equal(2, normalize.CallCount);          // P2-4: pass 1 mutates; the revalidation pass re-runs it (no-op fixed point)
        Assert.Equal(1, traversalBlocker.CallCount);   // reached on the revalidation pass and blocks the mutated args
        Assert.Equal(ToolGateAction.Allow, result.Rows[0].Baseline.Action);
        Assert.Equal(ToolGateAction.Block, result.Rows[0].Candidate.Action);
        Assert.True(result.Rows[0].Diverged);
    }

    [Fact]
    public async Task Mutate_WithNoLaterBlock_EffectiveActionIsMutate_CarryingFinalArgs()
    {
        var normalize = new RewriteArgsGate(new Dictionary<string, object?> { ["path"] = "/safe/notes.txt" });
        var calls = new[] { MakeCall("read_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }) };

        var result = await GateReplayer.CompareAsync(calls, baseline: [normalize], candidate: []);

        Assert.Equal(ToolGateAction.Mutate, result.Rows[0].Baseline.Action);
        Assert.Equal("/safe/notes.txt", result.Rows[0].Baseline.NewArguments?["path"]);
        Assert.Equal(ToolGateAction.Allow, result.Rows[0].Candidate.Action);
        Assert.True(result.Rows[0].Diverged);
    }

    [Fact]
    public async Task Mutate_IntroducingForbiddenPattern_CaughtByEarlierGate_OnRevalidation_ParityWithLive()
    {
        // P2-4 parity: an earlier-ordered gate Allowed the original args; a later mutator rewrites them into a
        // traversal. The replayer re-validates (just like the live loop) so the earlier gate re-runs against the
        // mutated args and blocks — the effective verdict is Block, not a smuggled-through Allow/Mutate.
        var traversalBlocker = new BlockIfArgContainsGate("..");
        var normalizer = new RewriteArgsGate(new Dictionary<string, object?> { ["path"] = "../../etc/passwd" });
        var calls = new[] { MakeCall("read_file", new Dictionary<string, object?> { ["path"] = "safe.txt" }) };

        var result = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [traversalBlocker, normalizer]);

        Assert.Equal(ToolGateAction.Block, result.Rows[0].Candidate.Action);
        Assert.True(result.Rows[0].Diverged);   // baseline (no gates) allowed; candidate blocks on revalidation
    }

    [Fact]
    public async Task Mutate_ThatNeverConverges_FailsClosed_InTheReplayer_Too()
    {
        var churn = new EverChangingRewriteGate();
        var calls = new[] { MakeCall("read_file", new Dictionary<string, object?> { ["path"] = "0" }) };

        var result = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [churn]);

        Assert.Equal(ToolGateAction.Block, result.Rows[0].Candidate.Action);   // non-convergence fails closed
    }

    [Fact]
    public async Task ThrowingGate_FailsClosedToBlock_DoesNotAbortTheWholeReplay()
    {
        // A gate that throws must fail closed to Block for its own row (live loop's cannot-inspect ⇒ deny) AND
        // must NOT propagate — propagation would drop every LATER captured call from the comparison entirely.
        var boom = new ThrowingGate();
        var calls = new[] { MakeCall("read_file"), MakeCall("send_email") };

        var result = await GateReplayer.CompareAsync(calls, baseline: [boom], candidate: []);

        Assert.Equal(2, result.Rows.Count);   // both calls evaluated — replay not aborted
        Assert.All(result.Rows, r => Assert.Equal(ToolGateAction.Block, r.Baseline.Action));
        Assert.All(result.Rows, r => Assert.Equal(ToolGateAction.Allow, r.Candidate.Action));
        Assert.All(result.Rows, r => Assert.True(r.Diverged));
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
