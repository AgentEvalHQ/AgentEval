// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 2, P2-8 — nested-run budget inheritance. A budget gate under <see cref="RunBudgetScope.Root"/> charges the
/// ROOT run's ledger, so a fan-out of nested sub-agent runs shares ONE budget and cannot launder past a parent cap;
/// under the default <see cref="RunBudgetScope.Current"/> each nested run keeps its own budget.
/// </summary>
public class NestedRunBudgetTests
{
    private static GatedToolCall Call(string tool = "t", IReadOnlyDictionary<string, object?>? args = null) =>
        new(tool, args, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    // ── AgentRunScope parent chain ──

    [Fact]
    public void TopLevelScope_HasNoParent_AndIsItsOwnRoot()
    {
        using var top = AgentRunScope.Begin(null, "top", null);
        Assert.Null(top.Parent);
        Assert.Same(top, top.Root);
    }

    [Fact]
    public void NestedScope_ParentIsEnclosing_RootIsOutermost()
    {
        using var top = AgentRunScope.Begin(null, "top", null);
        using var mid = AgentRunScope.Begin(null, "mid", null);
        using var leaf = AgentRunScope.Begin(null, "leaf", null);

        Assert.Same(mid, leaf.Parent);
        Assert.Same(top, mid.Parent);
        Assert.Same(top, leaf.Root);   // walks the whole chain to the outermost
        Assert.Same(top, mid.Root);
    }

    [Fact]
    public void ForRootRun_EqualsForCurrentRun_WhenNotNested()
    {
        using var top = AgentRunScope.Begin(null, "top", null);
        Assert.Same(RunLedger.ForCurrentRun(), RunLedger.ForRootRun());   // root IS current for an un-nested run
    }

    // ── RunBudgetGate ──

    [Fact]
    public async Task RunBudgetGate_RootScope_NestedRunSharesBudget_AndIsBlocked()
    {
        var gate = new RunBudgetGate(maxToolCalls: 2, budgetScope: RunBudgetScope.Root);

        using var parent = AgentRunScope.Begin(null, "parent", null);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);   // parent call 1
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);   // parent call 2 — budget now full

        using var nested = AgentRunScope.Begin(null, "child", null);
        // The nested run resolves to the SAME root ledger — the shared budget is already exhausted.
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call())).Action);
    }

    [Fact]
    public async Task RunBudgetGate_CurrentScope_NestedRunGetsFreshBudget()
    {
        // The contrast (and the laundering hole Root closes): the default Current scope gives each nested run its
        // OWN budget, so the parent being full does not stop a fresh nested run.
        var gate = new RunBudgetGate(maxToolCalls: 2, budgetScope: RunBudgetScope.Current);

        using var parent = AgentRunScope.Begin(null, "parent", null);
        await gate.InspectAsync(Call());
        await gate.InspectAsync(Call());   // parent budget full

        using var nested = AgentRunScope.Begin(null, "child", null);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call())).Action);   // fresh budget in the nested run
    }

    // ── MonetaryLimitGate ──

    [Fact]
    public async Task MonetaryLimitGate_RootScope_NestedRunSharesMonetaryCap()
    {
        var gate = new MonetaryLimitGate("amount", maxTotal: 1000m, budgetScope: RunBudgetScope.Root);
        var pay = Call("pay", new Dictionary<string, object?> { ["amount"] = 600m });

        using var parent = AgentRunScope.Begin(null, "parent", null);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(pay)).Action);   // running sum 600

        using var nested = AgentRunScope.Begin(null, "child", null);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(pay)).Action);   // 600 + 600 = 1200 > 1000, shared
    }

    // ── PerToolCallBudgetGate ──

    [Fact]
    public async Task PerToolCallBudgetGate_RootScope_NestedRunSharesPerToolCap()
    {
        var gate = new PerToolCallBudgetGate(new Dictionary<string, int> { ["delete"] = 1 }, budgetScope: RunBudgetScope.Root);
        var del = Call("delete");

        using var parent = AgentRunScope.Begin(null, "parent", null);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(del)).Action);   // 1st delete in the tree

        using var nested = AgentRunScope.Begin(null, "child", null);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(del)).Action);   // 2nd delete exhausts the shared cap
    }
}
