// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// P6-1 BlockStormSentinelGate — a meta-gate that trips once a run tree's ENFORCED-block volume
/// (RunLedger.TreeDenialCount, aggregated at the root) crosses a threshold, turning repeated denials (probing) into a
/// block/terminate + one atomically-latched incident alert.
/// </summary>
public class BlockStormSentinelGateTests
{
    private static GatedToolCall Call(string tool = "any_tool")
        => new(tool, new Dictionary<string, object?>(), "T", 0, 0, 1, false, Array.Empty<ChatMessage>());

    private static void RecordDenials(int n)
    {
        // Mirrors the enforced-block site (AgentEvalToolGateExtensions): a per-key attempts tally on the current run
        // PLUS the tree-wide block-storm total on the root — the latter is what the sentinel reads.
        for (var i = 0; i < n; i++)
        {
            RunLedger.ForCurrentRun().RecordDenial($"key-{i}");
            RunLedger.ForRootRun().RecordTreeDenial();
        }
    }

    [Fact]
    public async Task BelowThreshold_Allows()
    {
        using var scope = AgentRunScope.Begin(null, "T", null);
        RecordDenials(3);
        var gate = new BlockStormSentinelGate(threshold: 5);

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);
    }

    [Fact]
    public async Task AtThreshold_Blocks()
    {
        using var scope = AgentRunScope.Begin(null, "T", null);
        RecordDenials(5);
        var gate = new BlockStormSentinelGate(threshold: 5);

        var verdict = await gate.InspectAsync(Call());
        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Equal("BlockStormSentinel", verdict.PolicyName);
    }

    [Fact]
    public async Task AboveThreshold_Blocks()
    {
        using var scope = AgentRunScope.Begin(null, "T", null);
        RecordDenials(9);
        var gate = new BlockStormSentinelGate(threshold: 5);

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call())).Action);
    }

    [Fact]
    public async Task Alert_FiresOnce_OnTheTransition_WithIncidentSeverity()
    {
        using var scope = AgentRunScope.Begin(null, "T", null);
        var incidents = new List<BlockStormIncident>();
        var gate = new BlockStormSentinelGate(threshold: 5, onBlockStorm: incidents.Add);

        RecordDenials(4);
        await gate.InspectAsync(Call());   // 4 < 5 → allow, no alert

        RecordDenials(1);                  // now 5
        await gate.InspectAsync(Call());   // == threshold → block + alert
        RecordDenials(1);                  // now 6
        await gate.InspectAsync(Call());   // > threshold → block, NO second alert

        Assert.Single(incidents);
        Assert.Equal(5, incidents[0].EnforcedBlockCount);
        Assert.Equal(5, incidents[0].Threshold);
        Assert.Equal(GateSeverity.Incident, incidents[0].Severity);
        Assert.Equal(scope.RunId, incidents[0].RunId);
    }

    [Fact]
    public async Task Alert_FiresOnce_EvenWhenTallyJumpsPastThreshold()
    {
        // Audit-MEDIUM regression: the old "== threshold" check would MISS the alert when the tally jumped past the
        // exact threshold (concurrency, or the sentinel not seeing every denial). The atomic latch fires once on the
        // first crossing regardless of the jump.
        using var scope = AgentRunScope.Begin(null, "T", null);
        var incidents = new List<BlockStormIncident>();
        var gate = new BlockStormSentinelGate(threshold: 5, onBlockStorm: incidents.Add);

        RecordDenials(4);
        await gate.InspectAsync(Call());   // 4 < 5 → allow

        RecordDenials(2);                  // jumps 4 → 6, never landing exactly on 5
        await gate.InspectAsync(Call());   // 6 ≥ 5 → block + alert (the old check would have missed this)
        await gate.InspectAsync(Call());   // still latched → no second alert

        Assert.Single(incidents);
        Assert.Equal(6, incidents[0].EnforcedBlockCount);
    }

    [Fact]
    public async Task NestedRuns_AggregateAtRoot_StormCannotBeLaunderedAcrossSubRuns()
    {
        // Audit-MEDIUM regression: a per-current-run tally would let an attacker spread denials across nested
        // sub-agent runs, each under threshold. The tree-root aggregation (ForRootRun) catches the total.
        var gate = new BlockStormSentinelGate(threshold: 5);
        using var parent = AgentRunScope.Begin(null, "parent", null);
        RecordDenials(3);   // 3 in the parent (tree root)

        using var child = AgentRunScope.Begin(null, "child", null);   // nested: child.Root == parent
        RecordDenials(2);   // 2 more, accumulated at the same tree root → total 5

        // The sentinel in the CHILD run sees the whole tree's 5, not just the child's 2.
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call())).Action);
    }

    [Fact]
    public async Task ThrowingAlertSink_DoesNotBreakTheGate()
    {
        using var scope = AgentRunScope.Begin(null, "T", null);
        RecordDenials(5);
        var gate = new BlockStormSentinelGate(threshold: 5, onBlockStorm: _ => throw new InvalidOperationException("sink boom"));

        var verdict = await gate.InspectAsync(Call());   // must still block, not propagate the sink's throw
        Assert.Equal(ToolGateAction.Block, verdict.Action);
    }

    [Fact]
    public async Task NoRunScope_IsNoOp_Allows()
    {
        // No AgentRunScope ⇒ no per-run tally to read ⇒ the sentinel can't observe a storm ⇒ allow.
        var gate = new BlockStormSentinelGate(threshold: 1);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);
    }

    [Fact]
    public async Task Tally_IsPerRun_DoesNotBleedAcrossRuns()
    {
        using (var run1 = AgentRunScope.Begin(null, "T", null))
        {
            RecordDenials(9);
        }

        using (var run2 = AgentRunScope.Begin(null, "T", null))
        {
            var gate = new BlockStormSentinelGate(threshold: 5);
            Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);   // run1's storm stays in run1
        }
    }

    [Fact]
    public void Threshold_MustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BlockStormSentinelGate(threshold: 0));

    [Fact]
    public void PureCode_Cost()
        => Assert.Equal(GateCost.PureCode, new BlockStormSentinelGate().Cost);
}
