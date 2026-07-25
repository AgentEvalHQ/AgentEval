// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 3, F-C — the unified <see cref="GateEvidence"/> record and its supporting pieces (P3-2 complete block
/// evidence, P3-3 persisted provenance, P3-5 severity, P3-6 config fingerprint, the run identity). The key
/// invariant: enrichment is a SUPERSET projection that stays compatible with the existing readers.
/// </summary>
public class GateEvidenceTests
{
    // ── P3-6: config fingerprint ──

    [Fact]
    public void ConfigFingerprint_IsStable_AndOrderSensitive()
    {
        var a = new ForbiddenToolGate("x");
        var b = new ArgumentPatternGate("y");
        var f1 = GateConfigFingerprint.Compute(new IToolGate[] { a, b });
        var f2 = GateConfigFingerprint.Compute(new IToolGate[] { a, b });
        var f3 = GateConfigFingerprint.Compute(new IToolGate[] { b, a });

        Assert.Equal(f1, f2);        // deterministic
        Assert.NotEqual(f1, f3);     // order is semantically significant
        Assert.False(string.IsNullOrEmpty(f1));
    }

    // ── P3-5: severity taxonomy ──

    [Theory]
    [InlineData("RunBudgetGate", GateSeverity.Routine)]
    [InlineData("MonetaryLimitGate", GateSeverity.Routine)]
    [InlineData("TaintTrackingGate", GateSeverity.Suspicious)]
    [InlineData("SequenceGate", GateSeverity.Suspicious)]
    [InlineData("QuarantineLeaseGate", GateSeverity.Suspicious)]
    [InlineData("CanaryToolGate", GateSeverity.Incident)]
    public void Severity_ClassifiesByPolicyName(string policy, GateSeverity expected)
        => Assert.Equal(expected, GateSeverityClassifier.Classify(policy));

    [Fact]
    public void Severity_FailedClosedOnThrow_IsAlwaysIncident()
        => Assert.Equal(GateSeverity.Incident, GateSeverityClassifier.Classify("RunBudgetGate", failedClosedOnThrow: true));

    // ── Run identity ──

    [Fact]
    public void AgentRunScope_RunId_IsStableWithinScope_AndUniquePerScope()
    {
        using var outer = AgentRunScope.Begin(null, "a", null);
        var outerId = outer.RunId;
        Assert.Equal(outerId, AgentRunScope.Current!.RunId);   // stable while the scope is current

        using (var inner = AgentRunScope.Begin(null, "b", null))
        {
            Assert.NotEqual(outerId, inner.RunId);             // each run gets its own id
        }
    }

    // ── GateEvidence.ToMetadata projection ──

    [Fact]
    public void ToMetadata_KeepsReaderFields_AddsEnrichment_OmitsNulls()
    {
        var evidence = new GateEvidence
        {
            ReferenceId = "ref-1",
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Stage = "tool",
            Policy = "SomeGate",
            Action = "Block",
            Severity = GateSeverity.Incident,
            RunId = "run-1",
            AgentName = "Agatha",
            ToolName = "delete_account",
            Iteration = 3,
            Reason = "nope",
            ConfigFingerprint = "fp-abc",
        };

        Assert.Equal("gate.tool.7.SomeGate", evidence.TraceKey(7));

        var value = evidence.ToMetadata();
        Assert.Equal("Block", value["action"]);          // the field every reader keys on
        Assert.Equal("nope", value["reason"]);
        Assert.Equal("ref-1", value["referenceId"]);
        Assert.Equal("Incident", value["severity"]);
        Assert.Equal("run-1", value["runId"]);
        Assert.Equal("Agatha", value["agentName"]);
        Assert.Equal("delete_account", value["toolName"]);
        Assert.Equal(3, value["iteration"]);
        Assert.Equal("fp-abc", value["configFingerprint"]);
        Assert.False(string.IsNullOrEmpty((string?)value["tsUtc"]));
        Assert.DoesNotContain("correlationId", value.Keys);   // null optional field omitted
    }

    // ── Integration: a real tool-gate block carries complete evidence AND still counts as a block ──

    [Fact]
    public async Task ToolGateBlock_CarriesCompleteEnrichedEvidence_AndReaderStillCountsIt()
    {
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "Agatha", ChatOptions = new ChatOptions { Tools = [tool] } });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate()   // establishes the run scope so RunId is populated
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate, trace)
            .Build();

        await gated.RunAsync("go");

        // Reader compatibility: GlassBoxEvidence (via GateMetadataReader.IsBlock) still recognizes & counts the block.
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);

        var key = trace.Metadata!.Keys.Single(k => k.StartsWith("gate.tool.", StringComparison.Ordinal) && k.Contains("ForbiddenToolGate", StringComparison.Ordinal));
        var value = (IDictionary<string, object?>)trace.Metadata![key];
        Assert.Equal("Block", value["action"]);
        Assert.Equal("Agatha", value["agentName"]);
        Assert.Equal("delete_account", value["toolName"]);
        Assert.False(string.IsNullOrEmpty((string?)value["runId"]));
        Assert.False(string.IsNullOrEmpty((string?)value["configFingerprint"]));
        Assert.False(string.IsNullOrEmpty((string?)value["tsUtc"]));
        Assert.False(string.IsNullOrEmpty((string?)value["referenceId"]));
        Assert.Equal("Routine", value["severity"]);   // ForbiddenToolGate is an ordinary policy block
    }

    // ── Integration: P3-3 — a run-gate block persists the verdict's GateProvenance ──

    [Fact]
    public async Task RunGateBlock_PersistsGateProvenance()
    {
        var scripted = new ScriptedChatClient().AddText("some output");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "J" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate(post: [new ProvenanceGate()], policy: EvalGatePolicy.WarnOnly, trace: trace)
            .Build();

        await gated.RunAsync("go");

        var key = trace.Metadata!.Keys.Single(k => k.Contains("ProvenanceGate", StringComparison.Ordinal));
        var value = (IDictionary<string, object?>)trace.Metadata![key];
        var provenance = (IDictionary<string, object?>)value["provenance"]!;
        Assert.Equal("indirect-injection", provenance["ruleName"]);
        Assert.Equal(0.9, provenance["threshold"]);
        Assert.Equal(0.95, provenance["actualValue"]);
    }

    // A run gate whose Block verdict carries GateProvenance (like a CompositeJudgeGate does).
    private sealed class ProvenanceGate : IChatGate
    {
        public string PolicyName => "ProvenanceGate";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(GateVerdict.Block(PolicyName, "flagged") with
            {
                Provenance = new GateProvenance("indirect-injection", ["span-a"], Threshold: 0.9, ActualValue: 0.95),
            });
    }
}
