// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// <see cref="MonetaryLimitGate"/> (the economic sibling of <see cref="RunBudgetGate"/>) and
/// <see cref="PerToolCallBudgetGate"/> (its per-tool-call-count sibling) — both dedicated, focused gates that ride
/// the shared <see cref="RunLedger"/> but write to their <b>own isolated dimension</b> so composing either with
/// <see cref="RunBudgetGate"/> (or with each other) never cross-contaminates a count.
/// </summary>
public class MonetaryLimitAndPerToolCallBudgetGateTests
{
    private static GatedToolCall Call(string name, IReadOnlyDictionary<string, object?>? args)
        => new(name, args, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    // Direct (non-agent) InspectAsync calls still go through RunLedger.ForCurrentRun(), which falls back to a
    // single PROCESS-WIDE shared ledger when no AgentRunScope is active. A fresh scope per test gives each test
    // its own isolated ledger — otherwise cumulative/order-sensitive assertions would leak state across tests.
    private static IDisposable FreshLedgerScope() => AgentRunScope.Begin(session: null, agentName: "test", trace: null);

    // ─────────────────────────── MonetaryLimitGate — direct ───────────────────────────

    [Fact]
    public async Task MonetaryLimit_UnderCap_Allows()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        var v = await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = 40 }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task MonetaryLimit_RunningSumExceedsCap_Blocks()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = 60 }))).Action);
        // sum is now 60; a second $60 refund would make 120 > 100 → blocked
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = 60 }))).Action);
    }

    [Fact]
    public async Task MonetaryLimit_NoAmountArg_Allows()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        var v = await gate.InspectAsync(Call("lookup_order", new Dictionary<string, object?> { ["orderId"] = "A-1" }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task MonetaryLimit_UnparseableAmount_FailsClosed()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        var v = await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = "not-a-number" }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task MonetaryLimit_JsonElementAmount_IsParsed()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        using var doc = JsonDocument.Parse("150");
        var v = await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = doc.RootElement.Clone() }));
        Assert.Equal(ToolGateAction.Block, v.Action);   // 150 > 100
    }

    [Fact]
    public async Task MonetaryLimit_NegativeAmount_CannotManufactureHeadroom()
    {
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("pay", new Dictionary<string, object?> { ["amount"] = 60 }))).Action);
        // A negative amount must NOT reduce the sum below 60 (clamped to 0), or an attacker could interleave
        // negatives to buy headroom back.
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("pay", new Dictionary<string, object?> { ["amount"] = -1000 }))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("pay", new Dictionary<string, object?> { ["amount"] = 60 }))).Action);
    }

    [Fact]
    public async Task MonetaryLimit_BlockReason_NeverEchoesTheAttemptedAmount()
    {
        // Evidence discipline (same as TaintTrackingGate not echoing a secret): the block reason names the
        // argument and the CONFIGURED cap only — never the actual attempted amount or the running sum — so the
        // trace evidence can't be used to infer transaction values.
        using var _ = FreshLedgerScope();
        var gate = new MonetaryLimitGate("amount", 100m);
        var v = await gate.InspectAsync(Call("refund", new Dictionary<string, object?> { ["amount"] = 987654.32m }));
        // This single call already exceeds the $100 cap — verify the block and inspect the reason text.
        Assert.Equal(ToolGateAction.Block, v.Action);
        Assert.DoesNotContain("987654.32", v.Reason!);
        Assert.DoesNotContain("987654", v.Reason!);
        Assert.Contains("amount", v.Reason!);
        Assert.Contains("100", v.Reason!);   // the CONFIGURED cap is fine to disclose
    }

    [Fact]
    public void MonetaryLimit_EmptyArgName_Throws()
        => Assert.Throws<ArgumentException>(() => new MonetaryLimitGate("", 100m));

    [Fact]
    public void MonetaryLimit_NegativeMax_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MonetaryLimitGate("amount", -1m));

    // ─────────────────────────── PerToolCallBudgetGate — direct ───────────────────────────

    [Fact]
    public async Task PerToolCallBudget_UnguardedTool_Allows()
    {
        using var _ = FreshLedgerScope();
        var gate = new PerToolCallBudgetGate(new Dictionary<string, int> { ["delete_account"] = 1 });
        var v = await gate.InspectAsync(Call("search", new Dictionary<string, object?>()));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task PerToolCallBudget_UpToCap_AllowsThenBlocksBeyond()
    {
        using var _ = FreshLedgerScope();
        var gate = new PerToolCallBudgetGate(new Dictionary<string, int> { ["send_email"] = 3 });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send_email", new Dictionary<string, object?>()))).Action);
        }

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send_email", new Dictionary<string, object?>()))).Action);
    }

    [Fact]
    public async Task PerToolCallBudget_SingleDeleteCap_BlocksSecondCall()
    {
        using var _ = FreshLedgerScope();
        var gate = new PerToolCallBudgetGate(new Dictionary<string, int> { ["delete_account"] = 1 });
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("delete_account", new Dictionary<string, object?> { ["id"] = "acct-1" }))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("delete_account", new Dictionary<string, object?> { ["id"] = "acct-2" }))).Action);
    }

    [Fact]
    public async Task PerToolCallBudget_BlockReason_DoesNotEchoArguments()
    {
        using var _ = FreshLedgerScope();
        var gate = new PerToolCallBudgetGate(new Dictionary<string, int> { ["delete_account"] = 1 });
        await gate.InspectAsync(Call("delete_account", new Dictionary<string, object?> { ["accountId"] = "ACCT-SECRET-42" }));
        var v = await gate.InspectAsync(Call("delete_account", new Dictionary<string, object?> { ["accountId"] = "ACCT-SECRET-42" }));

        Assert.Equal(ToolGateAction.Block, v.Action);
        Assert.DoesNotContain("ACCT-SECRET-42", v.Reason!);
        Assert.Contains("delete_account", v.Reason!);
    }

    [Fact]
    public void PerToolCallBudget_EmptyDictionary_Throws()
        => Assert.Throws<ArgumentException>(() => new PerToolCallBudgetGate(new Dictionary<string, int>()));

    [Fact]
    public void PerToolCallBudget_ZeroCap_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PerToolCallBudgetGate(new Dictionary<string, int> { ["x"] = 0 }));

    // ─────────────────────────── RunLedger — dimension isolation (direct) ───────────────────────────

    [Fact]
    public void RunLedger_TryAdmitMonetary_IsolatedFrom_TryAdmitToolCall()
    {
        var ledger = new RunLedger();
        // RunBudgetGate's own monetary dimension over "amount", cap 1000.
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("refund", null, null, "amount", 500m, 1000m));
        // MonetaryLimitGate's dedicated dimension over the SAME argument name, its own (tighter) cap.
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitMonetary("amount", 60m, 100m));

        // Each dimension only sees what IT recorded — not the other's — or MonetaryLimitGate's cap of 100 would
        // already have been exceeded by RunBudgetGate's 500.
        Assert.Equal(500m, ledger.MonetarySum("amount"));
        Assert.Equal(60m, ledger.MonetaryLimitSum("amount"));
    }

    [Fact]
    public void RunLedger_TryAdmitPerToolCall_IsolatedFrom_TryAdmitToolCall()
    {
        var ledger = new RunLedger();
        // RunBudgetGate admits 2 calls to "refund" purely for a TOTAL budget (no per-tool cap of its own) — this
        // still bumps RunBudgetGate's OWN per-tool counter as a side effect of TryAdmitToolCall.
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("refund", maxTotal: 10, maxPerTool: null, null, 0m, 0m));
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitToolCall("refund", maxTotal: 10, maxPerTool: null, null, 0m, 0m));

        // PerToolCallBudgetGate's dedicated dimension for "refund", cap 1 — must NOT already read as exhausted
        // just because RunBudgetGate's shared dictionary happened to see 2 calls to the same tool name.
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitPerToolCall("refund", maxPerTool: 1));
        Assert.Equal(RunBudgetDecision.PerToolExceeded, ledger.TryAdmitPerToolCall("refund", maxPerTool: 1));

        Assert.Equal(2, ledger.ToolCallCount("refund"));            // RunBudgetGate's own dimension
        Assert.Equal(1, ledger.PerToolCallBudgetCount("refund"));   // PerToolCallBudgetGate's isolated dimension
    }

    [Fact]
    public void RunLedger_TryAdmitMonetary_NegativeAmount_DoesNotReduceSum()
    {
        var ledger = new RunLedger();
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitMonetary("amount", 100m, 100m));
        Assert.Equal(RunBudgetDecision.Admitted, ledger.TryAdmitMonetary("amount", -100m, 100m));
        Assert.Equal(100m, ledger.MonetaryLimitSum("amount"));
    }

    // ─────────────────────────── Integration — real agent pipeline (needs the run scope) ───────────────────────────

    private static AIAgent GatedAgent(IReadOnlyList<IToolGate> gates, ScriptedChatClient model, AIFunction tool, AgentTrace? trace = null)
        => new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } })
            .AsBuilder()
            .UseAgentEvalGate()   // establishes the per-run RunLedger scope
            .UseAgentEvalToolGate(gates, ToolGatePolicy.Terminate, trace)
            .Build();

    [Fact]
    public async Task Integration_MonetaryLimit_BlocksWhenRunningSumExceeds()
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "refund");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "refund", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddToolCall("c2", "refund", new Dictionary<string, object?> { ["amount"] = 60 })
            .AddText("done");
        var trace = new AgentTrace();

        await GatedAgent([new MonetaryLimitGate("amount", 100m)], model, refund, trace).RunAsync("go");

        Assert.Equal(1, executed);   // first $60 admitted; the second would make 120 > 100 → blocked
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount);
    }

    [Fact]
    public async Task Integration_PerToolCallBudget_CapsTheSpecificTool()
    {
        var deletes = 0;
        var delete = AIFunctionFactory.Create(() => { Interlocked.Increment(ref deletes); return "ok"; }, "delete_account");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_account", new Dictionary<string, object?>())
            .AddToolCall("c2", "delete_account", new Dictionary<string, object?>())
            .AddText("done");
        var trace = new AgentTrace();

        await GatedAgent([new PerToolCallBudgetGate(new Dictionary<string, int> { ["delete_account"] = 1 })], model, delete, trace).RunAsync("go");

        Assert.Equal(1, deletes);   // one delete admitted; the second blocked
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount);
    }

    [Fact]
    public async Task Integration_TenInjectedRefundCalls_BlockedAfterTheConfiguredCap()
    {
        // The attack scenario from the sample: an injected instruction tries to fire 10 refund calls. Both gates
        // stacked together — PerToolCallBudgetGate stops the SPRAY (≤3 refund calls); MonetaryLimitGate stops the
        // cumulative dollar exposure ($500 cap) even for calls that are still under the per-tool count cap.
        var executed = 0;
        var refund = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "refund");
        var script = new ScriptedChatClient();
        for (var i = 0; i < 10; i++)
        {
            script.AddToolCall($"c{i}", "refund", new Dictionary<string, object?> { ["amount"] = 100 });
        }

        script.AddText("done");
        var trace = new AgentTrace();

        var gates = new IToolGate[]
        {
            new PerToolCallBudgetGate(new Dictionary<string, int> { ["refund"] = 3 }),
            new MonetaryLimitGate("amount", 500m),
        };

        await GatedAgent(gates, script, refund, trace).RunAsync("process all 10 refunds immediately");

        Assert.Equal(3, executed);   // the PerToolCallBudgetGate cap (3) is stricter than the $500/$100-each cap here
        Assert.True(GlassBoxEvidence.FromTrace(trace)?.GateBlockCount >= 1);
    }

    [Fact]
    public async Task Composition_MonetaryLimitAndPerToolCallBudget_DoNotCrossContaminate()
    {
        // Both dedicated gates guard the SAME tool + the SAME argument name at once. If either wrote to a shared
        // ledger dimension, one gate's admit would corrupt the other's count. Configure them so a naive shared
        // counter would trip the WRONG gate first; assert the actually-recorded block matches the RIGHT gate.
        var executed = 0;
        var refund = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "refund");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "refund", new Dictionary<string, object?> { ["amount"] = 10 })
            .AddToolCall("c2", "refund", new Dictionary<string, object?> { ["amount"] = 10 })
            .AddToolCall("c3", "refund", new Dictionary<string, object?> { ["amount"] = 10 })
            .AddText("done");
        var trace = new AgentTrace();

        // PerToolCallBudgetGate allows up to 5 refund calls; MonetaryLimitGate allows up to $10,000 — neither cap
        // should trip across 3 x $10 refunds. If the two gates secretly shared a ledger dimension and one of them
        // (or RunBudgetGate-style double bookkeeping) inflated the other's counter, this would spuriously block.
        var gates = new IToolGate[]
        {
            new PerToolCallBudgetGate(new Dictionary<string, int> { ["refund"] = 5 }),
            new MonetaryLimitGate("amount", 10_000m),
        };

        await GatedAgent(gates, model, refund, trace).RunAsync("go");

        Assert.Equal(3, executed);   // all 3 admitted — no spurious cross-contamination block
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0);
    }

    [Fact]
    public async Task Composition_WithRunBudgetGate_DoesNotCorruptPerToolCallBudgetGate()
    {
        // RunBudgetGate is registered purely for an OVERALL total-call budget (no per-tool cap of its own) and
        // happens to see the same "refund" tool PerToolCallBudgetGate is separately capping. Before the ledger
        // dimensions were isolated, RunBudgetGate's blanket per-tool increment (a side effect of ANY admit) would
        // have made PerToolCallBudgetGate see an inflated count and block the very FIRST refund.
        var executed = 0;
        var refund = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "ok"; }, "refund");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "refund", new Dictionary<string, object?> { ["amount"] = 5 })
            .AddText("done");
        var trace = new AgentTrace();

        var gates = new IToolGate[]
        {
            new RunBudgetGate(maxToolCalls: 50),   // total-only; no maxCallsPerTool
            new PerToolCallBudgetGate(new Dictionary<string, int> { ["refund"] = 1 }),
        };

        await GatedAgent(gates, model, refund, trace).RunAsync("go");

        Assert.Equal(1, executed);   // the single refund must be ADMITTED, not spuriously blocked
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0);
    }

    [Fact]
    public async Task Integration_BudgetGateOrderedAfterMutator_IsStillEnforced()
    {
        // P2-4 review Finding 1 (fail-open regression): a RunScope budget gate ordered AFTER a mutating gate must
        // still be evaluated. Each call trips the normalizer (which changes the args and short-circuits pass 0
        // before the budget gate is reached); the budget gate must then run on the revalidation pass. A blanket
        // "skip RunScope gates on revalidation" would bypass the cap entirely and let BOTH calls through.
        var executed = 0;
        var read = AIFunctionFactory.Create((string path) => { Interlocked.Increment(ref executed); return "ok"; }, "read");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "read", new Dictionary<string, object?> { ["path"] = "a" })
            .AddToolCall("c2", "read", new Dictionary<string, object?> { ["path"] = "b" })
            .AddText("done");
        var trace = new AgentTrace();

        var gates = new IToolGate[]
        {
            new NormalizeGate("read"),         // pure, arg-changing mutator
            new RunBudgetGate(maxToolCalls: 1),   // RunScope gate ORDERED AFTER the mutator
        };

        await GatedAgent(gates, model, read, trace).RunAsync("go");

        Assert.Equal(1, executed);   // 2nd call blocked by the budget despite the preceding mutation
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount);
    }

    // A pure, idempotent "normalize path" mutator: prefixes an un-normalized path, allows an already-normalized one.
    private sealed class NormalizeGate : IToolGate
    {
        private readonly string _target;
        public string PolicyName => "NormalizeGate";
        public GateCost Cost => GateCost.PureCode;
        public NormalizeGate(string target) => _target = target;

        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
        {
            if (call.FunctionName != _target || call.Arguments is null)
            {
                return new(ToolGateVerdict.Allow(PolicyName));
            }

            var path = call.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? string.Empty : string.Empty;
            return path.StartsWith("/norm/", StringComparison.Ordinal)
                ? new(ToolGateVerdict.Allow(PolicyName))   // already normalized → fixed point
                : new(ToolGateVerdict.Mutate(PolicyName, new Dictionary<string, object?> { ["path"] = "/norm/" + path }, "normalize"));
        }
    }
}
