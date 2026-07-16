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

/// <summary>Gatekeeper Phase 1, #13 — TraceCaptureMode for mutation evidence (MutationEvidenceRenderer + the wired-in default).</summary>
public class TraceCaptureModeTests
{
    // ── MutationEvidenceRenderer unit coverage (internal type, exercised via the public reflection-free surface) ──

    private static ChatClientAgent BuildAgent(AIFunction tool, string toolName, IDictionary<string, object?> args)
    {
        var scripted = new ScriptedChatClient().AddToolCall("c1", toolName, args).AddText("done");
        return new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
    }

    private static async Task<IDictionary<string, object?>> RunMutateAndGetEvidence(TraceCaptureMode mode, string secretValue)
    {
        var tool = AIFunctionFactory.Create((string html) => "ok", "render");
        var agent = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "original" });
        var trace = new AgentTrace();
        var gate = new MutatingGate("render", new Dictionary<string, object?> { ["html"] = secretValue });
        var gated = agent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.WarnOnly, trace, mutationCaptureMode: mode).Build();

        await gated.RunAsync("go");

        return (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
    }

    [Fact]
    public async Task None_CapturesNothing()
    {
        var evidence = await RunMutateAndGetEvidence(TraceCaptureMode.None, "SECRET");
        Assert.DoesNotContain("SECRET", (string)evidence["argsAfter"]!);
        Assert.DoesNotContain("html", (string)evidence["argsAfter"]!);   // not even the key name
    }

    [Fact]
    public async Task SchemaOnly_CapturesKeyAndTypeOnly()
    {
        var evidence = await RunMutateAndGetEvidence(TraceCaptureMode.SchemaOnly, "SECRET");
        var after = (string)evidence["argsAfter"]!;
        Assert.DoesNotContain("SECRET", after);
        Assert.Contains("html", after);
        Assert.Contains("String", after);   // the CLR type name, not the value
    }

    [Fact]
    public async Task Redacted_CapturesKeyOnly_ValueIsFixedMarker()
    {
        var evidence = await RunMutateAndGetEvidence(TraceCaptureMode.Redacted, "SECRET");
        var after = (string)evidence["argsAfter"]!;
        Assert.DoesNotContain("SECRET", after);
        Assert.Contains("html", after);
        Assert.Contains("***", after);
    }

    [Fact]
    public async Task Hashed_CapturesStableHash_NotPlaintext()
    {
        var evidence1 = await RunMutateAndGetEvidence(TraceCaptureMode.Hashed, "SECRET");
        var evidence2 = await RunMutateAndGetEvidence(TraceCaptureMode.Hashed, "SECRET");
        var evidence3 = await RunMutateAndGetEvidence(TraceCaptureMode.Hashed, "DIFFERENT");

        var after1 = (string)evidence1["argsAfter"]!;
        var after2 = (string)evidence2["argsAfter"]!;
        var after3 = (string)evidence3["argsAfter"]!;

        Assert.DoesNotContain("SECRET", after1);
        Assert.Contains("sha256:", after1);
        Assert.Equal(after1, after2);         // same input ⇒ same hash (stable, comparable)
        Assert.NotEqual(after1, after3);      // different input ⇒ different hash
    }

    [Fact]
    public async Task Full_CapturesVerbatimValue()
    {
        var evidence = await RunMutateAndGetEvidence(TraceCaptureMode.Full, "SECRET-VALUE");
        Assert.Contains("SECRET-VALUE", (string)evidence["argsAfter"]!);
    }

    [Fact]
    public async Task DefaultMode_WhenNotSpecified_IsRedacted()
    {
        var tool = AIFunctionFactory.Create((string html) => "ok", "render");
        var agent = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "original" });
        var trace = new AgentTrace();
        var gate = new MutatingGate("render", new Dictionary<string, object?> { ["html"] = "SECRET" });
        var gated = agent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.WarnOnly, trace).Build();   // mutationCaptureMode omitted

        await gated.RunAsync("go");

        var evidence = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
        Assert.Equal(nameof(TraceCaptureMode.Redacted), evidence["captureMode"]);
        Assert.DoesNotContain("SECRET", (string)evidence["argsAfter"]!);
    }

    [Fact]
    public async Task NullArgumentValue_RemainsNull_AcrossAllModes()
    {
        foreach (var mode in new[] { TraceCaptureMode.SchemaOnly, TraceCaptureMode.Redacted, TraceCaptureMode.Hashed, TraceCaptureMode.Full })
        {
            var tool = AIFunctionFactory.Create((string? html) => "ok", "render");
            var agent = BuildAgent(tool, "render", new Dictionary<string, object?> { ["html"] = "x" });
            var trace = new AgentTrace();
            var gate = new MutatingGate("render", new Dictionary<string, object?> { ["html"] = null });
            var gated = agent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.WarnOnly, trace, mutationCaptureMode: mode).Build();

            await gated.RunAsync("go");

            var evidence = (IDictionary<string, object?>)trace.Metadata!["gate.tool.1.MutatingGate"];
            var after = (string)evidence["argsAfter"]!;
            Assert.Contains("null", after, StringComparison.OrdinalIgnoreCase);   // null is not a secret, always shown as-is (except SchemaOnly, which renders it as the literal string "null")
        }
    }

    // ── Test double ──

    private sealed class MutatingGate : IToolGate
    {
        private readonly string _target;
        private readonly IReadOnlyDictionary<string, object?> _newArgs;
        public string PolicyName => "MutatingGate";
        public GateCost Cost => GateCost.PureCode;
        public MutatingGate(string target, IReadOnlyDictionary<string, object?> newArgs) { _target = target; _newArgs = newArgs; }
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(call.FunctionName == _target
                ? ToolGateVerdict.Mutate(PolicyName, _newArgs, "sandboxed")
                : ToolGateVerdict.Allow(PolicyName));
    }
}
