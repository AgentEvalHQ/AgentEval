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

/// <summary>Phase 3 — P3-4 (alerting observer) and P3-11 (end-of-run receipt), both riding the F-C evidence stream.</summary>
public class GatekeeperObserverAndReceiptTests
{
    private sealed class RecordingObserver : IGatekeeperObserver
    {
        public List<GateEvidence> Findings { get; } = new();
        public void OnFinding(GateEvidence evidence) => Findings.Add(evidence);
    }

    // ── P3-4 ──

    [Fact]
    public void Observer_NotifiedOnActionableFindings_NotOnRoutineWarn()
    {
        var observer = new RecordingObserver();
        var sink = new ObserverEvidenceSink(observer);

        sink.Record(new GateEvidence { ReferenceId = "1", TimestampUtc = default, Stage = "tool", Policy = "P", Action = "Block", Severity = GateSeverity.Routine }, 1);
        sink.Record(new GateEvidence { ReferenceId = "2", TimestampUtc = default, Stage = "tool", Policy = "P", Action = "Redact", Severity = GateSeverity.Routine }, 2);
        sink.Record(new GateEvidence { ReferenceId = "3", TimestampUtc = default, Stage = "warning", Policy = "P", Action = "Warn", Severity = GateSeverity.Incident }, 3);   // incident → notified despite Warn
        sink.Record(new GateEvidence { ReferenceId = "4", TimestampUtc = default, Stage = "tool", Policy = "P", Action = "Warn", Severity = GateSeverity.Routine }, 4);   // routine warn → NOT notified

        Assert.Equal(3, observer.Findings.Count);
        Assert.DoesNotContain(observer.Findings, f => f.ReferenceId == "4");
    }

    [Fact]
    public async Task Observer_FiresOnAToolBlock_ThroughUseGatekeeper()
    {
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });
        var observer = new RecordingObserver();
        var trace = new AgentTrace();

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("delete_account"));
                g.Trace = trace;
                g.EvidenceSink = new ObserverEvidenceSink(observer);
            })
            .Build();

        await gated.RunAsync("go");

        Assert.Contains(observer.Findings, f => f.Action == "Block" && f.ToolName == "delete_account");
    }

    // ── P3-11 ──

    [Fact]
    public void RunLedger_Summarize_SnapshotsTotals()
    {
        var ledger = new RunLedger();
        ledger.RecordToolCall("read");
        ledger.RecordToolCall("read");
        ledger.RecordToolCall("write");

        var receipt = ledger.Summarize("run-1", "Agatha", "fp-x", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("run-1", receipt.RunId);
        Assert.Equal(3, receipt.TotalToolCalls);
        Assert.Equal(2, receipt.ToolCallCounts["read"]);
        Assert.Equal(1, receipt.ToolCallCounts["write"]);
    }

    [Fact]
    public async Task RunGate_EmitsReceipt_AtRunEnd()
    {
        var scripted = new ScriptedChatClient().AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "Agatha" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder().UseAgentEvalGate(trace: trace).Build();

        await gated.RunAsync("go");

        var key = trace.Metadata!.Keys.Single(k => k.StartsWith("gate.receipt.", StringComparison.Ordinal));
        var value = (IDictionary<string, object?>)trace.Metadata![key];
        Assert.Equal("Receipt", value["action"]);
        Assert.Equal("Agatha", value["agentName"]);
        Assert.Equal(0, value["totalToolCalls"]);   // no tools called this run
        Assert.False(string.IsNullOrEmpty((string?)value["runId"]));
    }

    [Fact]
    public async Task RunGate_EmitsReceipt_OnStreamingRun_Too()
    {
        // Phase 3 review #4: a streamed run must emit the same end-of-run receipt as a non-streaming one.
        var scripted = new ScriptedChatClient().AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "Agatha" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder().UseAgentEvalGate(trace: trace).Build();

        await foreach (var _ in gated.RunStreamingAsync("go")) { }

        Assert.Contains(trace.Metadata!.Keys, k => k.StartsWith("gate.receipt.", StringComparison.Ordinal));
    }

    // ── Phase 3 review #1: a throwing evidence sink must never break enforcement ──

    private sealed class ThrowingSink : IGateEvidenceSink
    {
        public void Record(GateEvidence evidence, int sequence) => throw new InvalidOperationException("sink boom");
    }

    [Fact]
    public async Task ThrowingEvidenceSink_DoesNotBreakEnforcement_NorPropagate()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string x) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("delete_account"));
                g.EvidenceSink = new ThrowingSink();   // every emission throws
            })
            .Build();

        var ex = await Record.ExceptionAsync(() => gated.RunAsync("go"));

        Assert.Null(ex);            // the throwing sink was isolated — no propagation out of the middleware
        Assert.Equal(0, executed);  // and enforcement still held: the forbidden tool never ran (no fail-open)
    }

    [Fact]
    public async Task ThrowingObserver_IsIsolated()
    {
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("delete_account"));
                g.EvidenceSink = new ObserverEvidenceSink(new ThrowingObserver());
            })
            .Build();

        Assert.Null(await Record.ExceptionAsync(() => gated.RunAsync("go")));
    }

    private sealed class ThrowingObserver : IGatekeeperObserver
    {
        public void OnFinding(GateEvidence evidence) => throw new InvalidOperationException("observer boom");
    }
}
