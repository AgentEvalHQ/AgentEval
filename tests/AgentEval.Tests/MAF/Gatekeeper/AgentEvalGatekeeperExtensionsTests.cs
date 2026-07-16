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

/// <summary>Gatekeeper Phase 1 — the composite builder (P0-5), no-implicit-default enforcement API (P0-2), and the missing-run-scope construction guard (P0-6).</summary>
public class AgentEvalGatekeeperExtensionsTests
{
    private static ChatClientAgent BuildAgent(AIFunction tool, string toolName, IDictionary<string, object?> args)
    {
        var scripted = new ScriptedChatClient().AddToolCall("call_1", toolName, args).AddText("done");
        return new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
    }

    // ── Enforcement modes actually enforce/observe ──

    [Fact]
    public async Task Terminate_BlocksToolCall()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string x) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g => { g.Add(new ForbiddenToolGate("delete_account")); g.Trace = trace; })
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
    }

    [Fact]
    public async Task Observe_RecordsButNeverBlocks()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string x) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Observe, g => { g.Add(new ForbiddenToolGate("delete_account")); g.Trace = trace; })
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, executed);   // observe mode never blocks
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace).GateBlockCount);   // recorded as Warn, not Block
    }

    [Fact]
    public async Task ReplaceResult_BlocksButDoesNotTerminateLoop()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string x) => { Interlocked.Increment(ref executed); return "ok"; }, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => { g.Add(new ForbiddenToolGate("delete_account")); g.Trace = trace; })
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);
        var value = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.ForbiddenToolGate"];
        Assert.False(value.ContainsKey("terminate"));
    }

    // ── ObserveWithAgentEvalGates / EnforceAgentEvalGates sugar ──

    [Fact]
    public async Task ObserveWithAgentEvalGates_PrintsBanner()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        using var writer = new StringWriter();
        var gated = agent.AsBuilder()
            .ObserveWithAgentEvalGates(g => { g.Add(new ForbiddenToolGate("delete_account")); g.BannerWriter = writer; })
            .Build();

        await gated.RunAsync("go");

        Assert.Contains("OBSERVE-ONLY MODE", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("NO TOOL CALLS WILL BE BLOCKED", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnforceAgentEvalGates_DoesNotPrintBanner()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        using var writer = new StringWriter();
        agent.AsBuilder().EnforceAgentEvalGates(g => { g.Add(new ForbiddenToolGate("delete_account")); g.BannerWriter = writer; }).Build();

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void EnforceAgentEvalGates_RejectsObserveLevel()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());
        var ex = Assert.Throws<ArgumentException>(() =>
            agent.AsBuilder().EnforceAgentEvalGates(g => g.Add(new ForbiddenToolGate("x")), GatekeeperEnforcement.Observe));
        Assert.Equal("level", ex.ParamName);
    }

    [Fact]
    public void EnforceAgentEvalGates_DefaultsToTerminate()
    {
        // Default level (no explicit `level:` argument) must be an enforcing one — verified indirectly via
        // the fact construction succeeds with a RunScope-requiring gate and EstablishRunScope defaulted true.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());
        var ex = Record.Exception(() => agent.AsBuilder().EnforceAgentEvalGates(g => g.Add(new RunBudgetGate(maxToolCalls: 5))).Build());
        Assert.Null(ex);
    }

    // ── P0-6: missing run-scope guard ──

    [Fact]
    public void RunScopeGate_EstablishRunScopeFalse_NoPrePost_Enforce_Throws()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new RunBudgetGate(maxToolCalls: 5));
                g.EstablishRunScope = false;
            }));

        Assert.Contains("RunBudgetGate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GateRequirements.RunScope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunScopeGate_EstablishRunScopeFalse_ObserveMode_DoesNotThrow()
    {
        // Advisory-only mode: the silent fallback is lower-stakes, so it's allowed to remain (per the design doc).
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Observe, g =>
            {
                g.Add(new RunBudgetGate(maxToolCalls: 5));
                g.EstablishRunScope = false;
            }));

        Assert.Null(ex);
    }

    [Fact]
    public void RunScopeGate_EstablishRunScopeFalse_ButPreGatePresent_DoesNotThrow()
    {
        // A pre/post gate forces UseAgentEvalGate to be called regardless, which establishes scope as an
        // unavoidable side effect — so the risk the guard exists for does not actually materialize here.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new RunBudgetGate(maxToolCalls: 5));
                g.AddPreGate(new AllowAllChatGate());
                g.EstablishRunScope = false;
            }));

        Assert.Null(ex);
    }

    [Fact]
    public void NonScopedGate_EstablishRunScopeFalse_Enforce_DoesNotThrow()
    {
        // ForbiddenToolGate does not declare GateRequirements.RunScope, so the guard has nothing to complain about.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("x"));
                g.EstablishRunScope = false;
            }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SequenceGate_ScopeEstablishedByDefault_WorksThroughComposite()
    {
        var reads = 0;
        var sends = 0;
        var readTool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref reads); return "secret"; }, "read_secrets");
        var sendTool = AIFunctionFactory.Create((string body) => { Interlocked.Increment(ref sends); return "sent"; }, "send_email");
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "read_secrets", new Dictionary<string, object?>())
            .AddToolCall("c2", "send_email", new Dictionary<string, object?> { ["body"] = "x" })
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [readTool, sendTool] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g => g.Add(new SequenceGate(["read_secrets"], ["send_email"])))
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(1, reads);
        Assert.Equal(0, sends);   // the default EstablishRunScope=true composed scope correctly — no manual UseAgentEvalGate() needed
    }

    // ── MinimumPolicy-floor vs Observe conflict guard: a canary/honeypot gate cannot be silently downgraded ──

    [Fact]
    public void FlooredGate_UnderObserve_ThrowsWithClearMessage()
    {
        // Observe always resolves to WarnOnly — a gate whose MinimumPolicy exceeds WarnOnly (a canary/honeypot
        // gate, where running under WarnOnly would silently defeat the trap) cannot be composed here at all.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Observe, g => g.Add(new FlooredToolGate())));

        Assert.Contains("FlooredToolGate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinimumPolicy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlooredGate_UnderReplaceResult_MeetsTheFloor_DoesNotThrow()
    {
        // FlooredToolGate needs ReplaceResult; ReplaceResult meets its own floor exactly.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => g.Add(new FlooredToolGate())));

        Assert.Null(ex);
    }

    [Fact]
    public void FlooredGate_UnderTerminate_DoesNotThrow()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g => g.Add(new FlooredToolGate())));

        Assert.Null(ex);
    }

    // ── Approval interop: only wired when a gate is actually registered ──

    [Fact]
    public void NoApprovalGates_DoesNotWireApprovalInterop_NoThrow()
    {
        // UseAgentEvalToolApproval throws on an EMPTY gate list (fail-closed by design); the composite must
        // simply skip wiring it when the caller registered none, not call through and blow up.
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g => g.Add(new ForbiddenToolGate("x"))).Build());

        Assert.Null(ex);
    }

    // ── P0-1 bundled: eager refuse-on-construction ──

    [Fact]
    public void RefuseUnprotectedHighRiskTools_NoKnownTools_Throws()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "x");
        var agent = BuildAgent(tool, "x", new Dictionary<string, object?>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("x"));
                g.RefuseUnprotectedHighRiskTools = true;
            }));

        Assert.Contains("KnownTools", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefuseUnprotectedHighRiskTools_UnprotectedHighRiskTool_ThrowsAtRegistration_BeforeBuild()
    {
        var deleteTool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(deleteTool, "delete_account", new Dictionary<string, object?>());

        var ex = Assert.Throws<UnprotectedHighRiskToolException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                // No tool gate registered at all — delete_account is high-risk and unprotected.
                g.KnownTools = [deleteTool];
                g.RefuseUnprotectedHighRiskTools = true;
            }));

        Assert.Contains("delete_account", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefuseUnprotectedHighRiskTools_Protected_DoesNotThrow()
    {
        var deleteTool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(deleteTool, "delete_account", new Dictionary<string, object?>());

        var ex = Record.Exception(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("delete_account"));
                g.KnownTools = [deleteTool];
                g.RefuseUnprotectedHighRiskTools = true;
            }));

        Assert.Null(ex);
    }

    // ── Telemetry threads through ──

    [Fact]
    public async Task Telemetry_WiredThroughComposite_RecordsInvocations()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "delete_account");
        var agent = BuildAgent(tool, "delete_account", new Dictionary<string, object?> { ["x"] = "1" });
        var telemetry = new GateTelemetry();

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
            {
                g.Add(new ForbiddenToolGate("delete_account"));
                g.Telemetry = telemetry;
            })
            .Build();

        await gated.RunAsync("go");

        var snapshot = Assert.Single(telemetry.Snapshot());
        Assert.Equal(1, snapshot.BlockCount);
    }

    // ── GatekeeperOptions sugar ──

    [Fact]
    public void OptionsSugar_AddMethods_PopulateCorrectLists()
    {
        var options = new GatekeeperOptions();
        options.Add(new ForbiddenToolGate("x"));
        options.AddPreGate(new AllowAllChatGate());
        options.AddPostGate(new AllowAllChatGate());
        options.AddApprovalGate(new AlwaysAutoApproveGate());

        Assert.Single(options.ToolGates);
        Assert.Single(options.PreGates);
        Assert.Single(options.PostGates);
        Assert.Single(options.ApprovalGates);
    }

    // ── Test doubles ──

    private sealed class AllowAllChatGate : IChatGate
    {
        public string PolicyName => "AllowAllChatGate";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(GateVerdict.Allow(PolicyName));
    }

    private sealed class AlwaysAutoApproveGate : IToolApprovalGate
    {
        public string PolicyName => "AlwaysAutoApproveGate";
        public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
            => new(true);
    }

    // A minimal stand-in for CanaryToolGate (which lives in AgentEval.RedTeam.Gatekeeper, not referenced here):
    // any gate that declares a MinimumPolicy floor above WarnOnly, the way a honeypot/canary must.
    private sealed class FlooredToolGate : IToolGate
    {
        public string PolicyName => "FlooredToolGate";
        public GateCost Cost => GateCost.PureCode;
        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(ToolGateVerdict.Allow(PolicyName));
    }
}
