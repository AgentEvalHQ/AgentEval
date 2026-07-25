// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 3, P3-1 — the referenceId ledger + resolver. A blocked call's opaque reference id resolves in O(1) to the
/// full <see cref="GateEvidence"/>, WORKS WITH TRACING OFF (the resolver is fed directly, not from the trace), is
/// bounded + TTL'd, and the JSONL index appends only enforced refusals.
/// </summary>
public class GateReferenceLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static GateEvidence Block(string referenceId) => new()
    {
        ReferenceId = referenceId,
        TimestampUtc = T0,
        Stage = "tool",
        Policy = "ForbiddenToolGate",
        Action = "Block",
        ToolName = "delete_account",
    };

    [Fact]
    public void Resolver_RecordThenResolve()
    {
        var resolver = new GateVerdictResolver(timeProvider: new TestClock(T0));
        resolver.Record(Block("ref-1"));

        Assert.True(resolver.TryResolve("ref-1", out var evidence));
        Assert.Equal("ForbiddenToolGate", evidence.Policy);
        Assert.False(resolver.TryResolve("nope", out _));
    }

    [Fact]
    public void Resolver_ExpiredEntry_NoLongerResolves()
    {
        var clock = new TestClock(T0);
        var resolver = new GateVerdictResolver(ttl: TimeSpan.FromMinutes(10), timeProvider: clock);
        resolver.Record(Block("ref-1"));

        Assert.True(resolver.TryResolve("ref-1", out _));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(resolver.TryResolve("ref-1", out _));   // TTL expired
    }

    [Fact]
    public void Resolver_EvictsOldest_PastCapacity()
    {
        var resolver = new GateVerdictResolver(maxEntries: 2, timeProvider: new TestClock(T0));
        resolver.Record(Block("a"));
        resolver.Record(Block("b"));
        resolver.Record(Block("c"));   // evicts "a"

        Assert.False(resolver.TryResolve("a", out _));
        Assert.True(resolver.TryResolve("b", out _));
        Assert.True(resolver.TryResolve("c", out _));
        Assert.Equal(2, resolver.Count);
    }

    [Fact]
    public void Ledger_JsonlIndex_AppendsOnlyBlocks()
    {
        var sb = new StringBuilder();
        var ledger = new GateReferenceLedger(new GateVerdictResolver(timeProvider: new TestClock(T0)), new StringWriter(sb));

        ledger.Record(Block("ref-block"), 1);
        ledger.Record(new GateEvidence { ReferenceId = "ref-warn", TimestampUtc = T0, Stage = "tool", Policy = "P", Action = "Warn" }, 2);

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);   // only the Block was indexed to JSONL
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("ref-block", doc.RootElement.GetProperty("referenceId").GetString());
        Assert.Equal("delete_account", doc.RootElement.GetProperty("toolName").GetString());

        // But BOTH are resolvable in memory (the resolver indexes every record).
        Assert.True(ledger.Resolver.TryResolve("ref-block", out _));
        Assert.True(ledger.Resolver.TryResolve("ref-warn", out _));
    }

    [Fact]
    public async Task Integration_ToolBlock_ResolvesReferenceId_WithTracingOFF()
    {
        // The core P3-1 promise: a block's reference id resolves to the full record even when NO trace is attached.
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });
        var index = new StringBuilder();
        var ledger = new GateReferenceLedger(new GateVerdictResolver(), new StringWriter(index));

        var gated = agent.AsBuilder()
            .UseAgentEvalGate(evidenceSink: ledger)   // establishes the run scope; feeds the ledger
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate, trace: null, evidenceSink: ledger)
            .Build();

        await gated.RunAsync("go");

        // The refusal was appended to the JSONL index; take its reference id and resolve the full record — with
        // tracing entirely off (the resolver was fed directly by the sink, not reconstructed from a trace).
        var line = index.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        using var entry = JsonDocument.Parse(line);
        var referenceId = entry.RootElement.GetProperty("referenceId").GetString()!;

        Assert.True(ledger.Resolver.TryResolve(referenceId, out var evidence));
        Assert.Equal("Block", evidence.Action);
        Assert.Equal("delete_account", evidence.ToolName);
        Assert.Equal("ForbiddenToolGate", evidence.Policy);
        Assert.False(string.IsNullOrEmpty(evidence.RunId));   // run scope stamped
    }

    private sealed class NullRedactGate : IToolResultGate
    {
        public string PolicyName => "NullRedactGate";
        public GateCost Cost => GateCost.PureCode;
        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;
        public ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken ct = default)
            => new(new ToolResultVerdict(ToolResultAction.Redact, PolicyName, "no replacement supplied", RedactedResult: null));
    }

    [Fact]
    public async Task NullRedactFallback_IsIndexedInDurableJsonl_AsARefusal()
    {
        // Phase 3 review #3: a null-redact fallback withholds the result and returns a model-facing refusal, so it
        // must be persisted to the durable index (which only records refusals) — not just resolvable in memory.
        var tool = AIFunctionFactory.Create((string x) => "the-secret", "fetch");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "fetch", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });
        var index = new StringBuilder();
        var ledger = new GateReferenceLedger(new GateVerdictResolver(), new StringWriter(index));

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => { g.AddResultGate(new NullRedactGate()); g.EvidenceSink = ledger; })
            .Build();

        await gated.RunAsync("go");

        var lines = index.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);   // the withheld-result refusal IS in the durable index
        Assert.Contains("NullRedactGate", lines[0]);
    }
}
