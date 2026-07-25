// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, P3-8 — cross-run aggregation over the durable JSONL refusal index a GateReferenceLedger writes.</summary>
public class GateReferenceIndexAggregatorTests
{
    private static GateReferenceLedger.GateReferenceIndexEntry Entry(string policy, string? tool, string config, string severity, DateTimeOffset ts) =>
        new("ref-" + Guid.Empty, ts, "run-x", policy, tool is null ? "run-pre" : "tool", tool, "A", severity, config);

    [Fact]
    public void Aggregate_BreaksDownByPolicyToolConfigSeverityAndDay()
    {
        var d1 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            Entry("ForbiddenToolGate", "delete_account", "fp-1", "Routine", d1),
            Entry("ForbiddenToolGate", "delete_account", "fp-1", "Routine", d1),
            Entry("TaintTrackingGate", "http_post", "fp-1", "Suspicious", d1),
            Entry("CanaryToolGate", "canary", "fp-2", "Incident", d2),
        };

        var agg = GateReferenceIndexAggregator.Aggregate(entries);

        Assert.Equal(4, agg.Total);
        Assert.Equal(2, agg.ByPolicy["ForbiddenToolGate"]);
        Assert.Equal(2, agg.ByTool["delete_account"]);
        Assert.Equal(3, agg.ByConfigFingerprint["fp-1"]);
        Assert.Equal(1, agg.ByConfigFingerprint["fp-2"]);
        Assert.Equal(1, agg.BySeverity["Incident"]);
        Assert.Equal(3, agg.ByDay["2026-01-01"]);
        Assert.Equal(1, agg.ByDay["2026-01-02"]);
    }

    [Fact]
    public void Read_ParsesJsonl_SkipsBlankAndMalformedLines()
    {
        var jsonl = new StringBuilder()
            .AppendLine("""{"referenceId":"a","tsUtc":"2026-01-01T00:00:00+00:00","policy":"P","stage":"tool","severity":"Routine","toolName":"t","configFingerprint":"fp-1"}""")
            .AppendLine("")                       // blank → skipped
            .AppendLine("not json at all {")      // malformed → skipped, not fatal
            .AppendLine("""{"referenceId":"b","tsUtc":"2026-01-01T00:00:00+00:00","policy":"P","stage":"tool","severity":"Routine","toolName":"t","configFingerprint":"fp-1"}""")
            .ToString();

        var entries = GateReferenceIndexAggregator.Read(new StringReader(jsonl));
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, GateReferenceIndexAggregator.Aggregate(entries).ByConfigFingerprint["fp-1"]);
    }

    [Fact]
    public async Task Integration_RealRunWritesIndex_ThenAggregatesAcrossRuns()
    {
        // Two runs (two blocks) append to ONE durable index; aggregation reads it back offline.
        var index = new StringBuilder();
        var ledger = new GateReferenceLedger(new GateVerdictResolver(), new StringWriter(index));

        for (var i = 0; i < 2; i++)
        {
            var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
            var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
            var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });
            await agent.AsBuilder()
                .UseAgentEvalGate(evidenceSink: ledger)
                .UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate, trace: null, evidenceSink: ledger)
                .Build()
                .RunAsync("go");
        }

        var agg = GateReferenceIndexAggregator.ReadAndAggregate(new StringReader(index.ToString()));
        Assert.Equal(2, agg.Total);
        Assert.Equal(2, agg.ByPolicy["ForbiddenToolGate"]);
        Assert.Equal(2, agg.ByTool["delete_account"]);
    }
}
