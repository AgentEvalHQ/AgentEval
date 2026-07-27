// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.6 — camouflage changes refusal presentation only.</summary>
public sealed class CamouflagedRefusalTests
{
    private const string Camouflage = "The operation could not be completed.";
    private const string AlternateCamouflage = "The service is temporarily unavailable.";

    [Fact]
    public async Task ToolBlock_CamouflageChangesOnlyPresentationAndPreservesEvidenceAndAttempts()
    {
        var structured = await RunToolBlockAsync(GatekeeperRefusalStyle.Structured);
        var camouflaged = await RunToolBlockAsync(
            GatekeeperRefusalStyle.Camouflaged,
            [Camouflage, AlternateCamouflage]);

        Assert.True(GatekeeperRefusalContract.TryParse(
            structured.Body,
            out var structuredReference,
            out var structuredDisposition,
            out var structuredAttempts));
        Assert.Equal(RefusalDisposition.Denied, structuredDisposition);
        Assert.Equal(1, structuredAttempts);
        Assert.Equal(structured.Evidence.ReferenceId, structuredReference);

        Assert.Contains(camouflaged.Body, new[] { Camouflage, AlternateCamouflage });
        Assert.False(GatekeeperRefusalContract.TryParse(camouflaged.Body, out _, out _, out _));
        Assert.DoesNotContain(camouflaged.Evidence.ReferenceId, camouflaged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("SensitivePolicy", camouflaged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-reason", camouflaged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("17", camouflaged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret", camouflaged.Body, StringComparison.Ordinal);

        Assert.Equal(structured.Evidence.Stage, camouflaged.Evidence.Stage);
        Assert.Equal(structured.Evidence.Policy, camouflaged.Evidence.Policy);
        Assert.Equal(structured.Evidence.Action, camouflaged.Evidence.Action);
        Assert.Equal(structured.Evidence.Reason, camouflaged.Evidence.Reason);
        Assert.Equal(structured.Evidence.ToolName, camouflaged.Evidence.ToolName);
        Assert.Equal(structured.Evidence.ConfigFingerprint, camouflaged.Evidence.ConfigFingerprint);
        Assert.Equal(1, Assert.IsType<int>(structured.Evidence.Extra!["attempts"]));
        Assert.Equal(1, Assert.IsType<int>(camouflaged.Evidence.Extra!["attempts"]));
    }

    [Fact]
    public async Task RunPreBlock_UsesCamouflageWhileEvidenceRetainsReferenceAndReason()
    {
        var scripted = new ScriptedChatClient().AddText("model should not run");
        var sink = new CapturingSink();
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "agent" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                ConfigureCamouflage(options);
                options.AddPreGate(new BlockingChatGate());
                options.EvidenceSink = sink;
            })
            .Build();

        var response = await agent.RunAsync("unsafe input");

        Assert.Equal(Camouflage, response.Text);
        Assert.False(GatekeeperRefusalContract.TryParse(response.Text, out _, out _, out _));
        var evidence = Assert.Single(sink.Records, item => item.Stage == "run-pre");
        Assert.Equal("Block", evidence.Action);
        Assert.Equal("RunSensitivePolicy", evidence.Policy);
        Assert.Equal("secret-run-reason", evidence.Reason);
        Assert.StartsWith("gk_", evidence.ReferenceId, StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.ReferenceId, response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultBlockAndNullRedactionFallback_UseCamouflage()
    {
        var blocked = await RunResultGateAsync(new BlockingResultGate());
        var nullRedaction = await RunResultGateAsync(new NullRedactionResultGate());

        Assert.Equal(Camouflage, blocked.Body);
        Assert.Equal(Camouflage, nullRedaction.Body);
        Assert.Equal("Block", blocked.Evidence.Action);
        Assert.Equal("Block", nullRedaction.Evidence.Action);
        Assert.Equal("tool-result", blocked.Evidence.Stage);
        Assert.Equal("tool-result", nullRedaction.Evidence.Stage);
    }

    [Fact]
    public async Task ThrowingToolGate_FailsClosedWithCamouflageAndFullEvidence()
    {
        var result = await RunToolGateAsync(new ThrowingToolGate());

        Assert.Equal(Camouflage, result.Body);
        Assert.Equal("Block", result.Evidence.Action);
        Assert.Equal("ThrowingSensitivePolicy", result.Evidence.Policy);
        Assert.Contains("threw", result.Evidence.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingSensitivePolicy", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence.ReferenceId, result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutationNonConvergence_FailsClosedWithCamouflage()
    {
        var result = await RunToolGateAsync(new OscillatingMutationGate());

        Assert.Equal(Camouflage, result.Body);
        Assert.Equal("Block", result.Evidence.Action);
        Assert.Equal("MutationRevalidation", result.Evidence.Policy);
        Assert.Contains("did not converge", result.Evidence.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("MutationRevalidation", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitSafeRedaction_RemainsUnchangedInCamouflagedMode()
    {
        var scripted = new ScriptedChatClient().AddText("unsafe model response");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "agent" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                ConfigureCamouflage(options);
                options.PostGates.Add(new SafeRedactingChatGate());
            })
            .Build();

        var response = await agent.RunAsync("go");

        Assert.Equal("caller-safe response", response.Text);
        Assert.NotEqual(Camouflage, response.Text);
        Assert.False(GatekeeperRefusalContract.TryParse(response.Text, out _, out _, out _));
    }

    [Fact]
    public async Task CallerPoolMutationAfterConstruction_CannotChangePresentation()
    {
        var messages = new List<string> { Camouflage };
        var (inner, scripted) = ToolAgent();
        var gated = inner.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.RefusalStyle = GatekeeperRefusalStyle.Camouflaged;
                options.CamouflagedRefusalMessages = messages;
                options.Add(new SensitiveBlockGate());
            })
            .Build();
        messages[0] = "caller mutation";

        await gated.RunAsync("go");

        Assert.Equal(Camouflage, ToolResult(scripted));
    }

    private static async Task<(string Body, GateEvidence Evidence)> RunToolBlockAsync(
        GatekeeperRefusalStyle style,
        IReadOnlyList<string>? messages = null)
    {
        var (inner, scripted) = ToolAgent();
        var sink = new CapturingSink();
        var gated = inner.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.RefusalStyle = style;
                options.CamouflagedRefusalMessages = messages;
                options.Add(new SensitiveBlockGate());
                options.EvidenceSink = sink;
            })
            .Build();

        await gated.RunAsync("go");

        return (
            ToolResult(scripted),
            Assert.Single(sink.Records, item => item.Stage == "tool" && item.Action == "Block"));
    }

    private static async Task<(string Body, GateEvidence Evidence)> RunToolGateAsync(IToolGate gate)
    {
        var (inner, scripted) = ToolAgent();
        var sink = new CapturingSink();
        var gated = inner.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                ConfigureCamouflage(options);
                options.Add(gate);
                options.EvidenceSink = sink;
            })
            .Build();

        await gated.RunAsync("go");

        return (
            ToolResult(scripted),
            Assert.Single(sink.Records, item => item.Stage == "tool" && item.Action == "Block"));
    }

    private static async Task<(string Body, GateEvidence Evidence)> RunResultGateAsync(IToolResultGate gate)
    {
        var (inner, scripted) = ToolAgent(toolResult: "sensitive tool result");
        var sink = new CapturingSink();
        var gated = inner.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                ConfigureCamouflage(options);
                options.AddResultGate(gate);
                options.EvidenceSink = sink;
            })
            .Build();

        await gated.RunAsync("go");

        return (
            ToolResult(scripted),
            Assert.Single(sink.Records, item => item.Stage == "tool-result" && item.Action == "Block"));
    }

    private static (ChatClientAgent Agent, ScriptedChatClient Scripted) ToolAgent(
        string toolResult = "executed")
    {
        var scripted = new ScriptedChatClient()
            .AddToolCall(
                "call-1",
                "dangerous",
                new Dictionary<string, object?> { ["value"] = "initial" })
            .AddText("done");
        var tool = AIFunctionFactory.Create((string value) => toolResult + value, "dangerous");
        var agent = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        return (agent, scripted);
    }

    private static string ToolResult(ScriptedChatClient scripted)
        => scripted.ReceivedMessages
            .SelectMany(messages => messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => result.Result?.ToString())
            .Last(result => result is not null)!;

    private static void ConfigureCamouflage(GatekeeperOptions options)
    {
        options.RefusalStyle = GatekeeperRefusalStyle.Camouflaged;
        options.CamouflagedRefusalMessages = [Camouflage];
    }

    private sealed class CapturingSink : IGateEvidenceSink
    {
        private readonly object _lock = new();
        private readonly List<GateEvidence> _records = [];

        public IReadOnlyList<GateEvidence> Records
        {
            get { lock (_lock) { return [.. _records]; } }
        }

        public void Record(GateEvidence evidence, int sequence)
        {
            lock (_lock)
            {
                _records.Add(evidence);
            }
        }
    }

    private sealed class SensitiveBlockGate : IToolGate
    {
        public string PolicyName => "SensitivePolicy";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => new(ToolGateVerdict.Block(
                PolicyName,
                "secret-reason threshold=17 target=tenant-secret"));
    }

    private sealed class ThrowingToolGate : IToolGate
    {
        public string PolicyName => "ThrowingSensitivePolicy";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("secret throwing reason");
    }

    private sealed class OscillatingMutationGate : IToolGate
    {
        private int _calls;

        public string PolicyName => "OscillatingMutation";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            var value = Interlocked.Increment(ref _calls) % 2 == 0 ? "left" : "right";
            return new(ToolGateVerdict.Mutate(
                PolicyName,
                new Dictionary<string, object?> { ["value"] = value },
                "keep changing"));
        }
    }

    private sealed class BlockingResultGate : IToolResultGate
    {
        public string PolicyName => "ResultSensitivePolicy";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolResultVerdict> InspectAsync(
            GatedToolResult result,
            CancellationToken cancellationToken = default)
            => new(ToolResultVerdict.Block(PolicyName, "secret-result-reason"));
    }

    private sealed class NullRedactionResultGate : IToolResultGate
    {
        public string PolicyName => "NullRedactionSensitivePolicy";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolResultVerdict> InspectAsync(
            GatedToolResult result,
            CancellationToken cancellationToken = default)
            => new(new ToolResultVerdict(
                ToolResultAction.Redact,
                PolicyName,
                "secret-null-redaction-reason",
                RedactedResult: null));
    }

    private sealed class BlockingChatGate : IChatGate
    {
        public string PolicyName => "RunSensitivePolicy";

        public ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
            => new(GateVerdict.Block(PolicyName, "secret-run-reason"));
    }

    private sealed class SafeRedactingChatGate : IChatGate
    {
        public string PolicyName => "SafeRedactingPolicy";

        public ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
            => new(GateVerdict.Block(
                PolicyName,
                "secret-run-reason",
                redactedText: "caller-safe response"));
    }
}
