// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.AgentHooks;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks;

/// <summary>
/// S1 — Gatekeeper exposed as an AGENT-HOOKS-0.1 <c>IInterceptor</c>.
/// Contexts are built with the SDK's own <c>AgentContextBuilder</c> so the fixtures are spec-shaped rather
/// than hand-rolled JSON that might drift from §4.
/// </summary>
public sealed class GatekeeperInterceptorTests
{
    private static AgentContextBuilder Builder() =>
        new("agenteval-test", "agenteval", "session-1", "TestAgent", "1.0", "2026-08-09T00:00:00Z");

    private static AgentContext PreToolCall(string name, JsonObject? args = null) =>
        Builder().PreToolCall("tc-1", name, args ?? new JsonObject { ["path"] = "/tmp/file" });

    /// <summary>A gate that always returns the supplied verdict, for exercising the mapping.</summary>
    private sealed class StubGate(ToolGateVerdict verdict, string policy = "StubPolicy") : IToolGate
    {
        public string PolicyName => policy;
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(verdict);
    }

    [Fact]
    public async Task PreToolCall_AllGatesAllow_ReturnsAllow()
    {
        var interceptor = new GatekeeperInterceptor([new StubGate(ToolGateVerdict.Allow("A"))]);

        var verdict = await interceptor.InterceptAsync(PreToolCall("read_file"), CancellationToken.None);

        Assert.Equal(Decision.Allow, verdict.Decision);
        verdict.Validate();   // must be a spec-valid verdict
    }

    [Fact]
    public async Task PreToolCall_BlockingGate_ReturnsDeny_WithNonReservedReason()
    {
        var interceptor = new GatekeeperInterceptor(
            [new StubGate(ToolGateVerdict.Block("ForbiddenTool", "delete_all is never permitted"), "ForbiddenTool")]);

        var verdict = await interceptor.InterceptAsync(PreToolCall("delete_all"), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.Contains("delete_all is never permitted", verdict.Message);
        // §5: the host_error: prefix is reserved for host-synthesized failures — an interceptor must not use it.
        Assert.DoesNotContain("host_error:", verdict.Reason);
        verdict.Validate();
    }

    [Fact]
    public async Task PreToolCall_FirstNonAllowWins_LaterGatesNeverRun()
    {
        // Mirrors AgentEvalToolGateExtensions: STRICT SEQUENTIAL, first-non-Allow-wins.
        var secondGateRan = false;
        var interceptor = new GatekeeperInterceptor(
        [
            new StubGate(ToolGateVerdict.Block("First", "stop here"), "First"),
            new ThrowIfRunGate(() => secondGateRan = true),
        ]);

        var verdict = await interceptor.InterceptAsync(PreToolCall("anything"), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.False(secondGateRan);
    }

    private sealed class ThrowIfRunGate(Action onRun) : IToolGate
    {
        public string PolicyName => "MustNotRun";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
        {
            onRun();
            return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
        }
    }

    [Fact]
    public async Task PreToolCall_MutatingGate_ReturnsTransformRootedAtTarget()
    {
        // §4.2 fixes target = tool_call.args at pre_tool_call, so the rewrite is rooted at $target.
        var rewritten = new Dictionary<string, object?> { ["path"] = "/tmp/safe" };
        var interceptor = new GatekeeperInterceptor(
            [new StubGate(ToolGateVerdict.Mutate("Redactor", rewritten, "path narrowed"), "Redactor")]);

        var verdict = await interceptor.InterceptAsync(PreToolCall("read_file"), CancellationToken.None);

        Assert.Equal(Decision.Transform, verdict.Decision);
        Assert.NotNull(verdict.Transform);
        Assert.Equal("$target", verdict.Transform!.Path);
        Assert.Equal("/tmp/safe", verdict.Transform.Value!["path"]!.GetValue<string>());
        verdict.Validate();
    }

    [Fact]
    public async Task NonToolPoint_ReturnsAllow_ButWarnsItWasNotExamined()
    {
        // The honesty case: an allow from a seam with no gate must not read as "checked and safe".
        var interceptor = new GatekeeperInterceptor([new StubGate(ToolGateVerdict.Allow("A"))]);

        var verdict = await interceptor.InterceptAsync(Builder().Output(JsonValue.Create("done")), CancellationToken.None);

        Assert.Equal(Decision.Allow, verdict.Decision);
        Assert.Contains(verdict.Warnings, w => w.Reason.Contains("point_not_enforced"));
        verdict.Validate();
    }

    [Fact]
    public async Task NoGatesRegistered_ReturnsAllow_ButWarnsNothingExaminedIt()
    {
        var interceptor = new GatekeeperInterceptor([]);

        var verdict = await interceptor.InterceptAsync(PreToolCall("read_file"), CancellationToken.None);

        Assert.Equal(Decision.Allow, verdict.Decision);
        Assert.Contains(verdict.Warnings, w => w.Reason.Contains("no_gates_registered"));
        verdict.Validate();
    }

    [Fact]
    public async Task PreToolCall_WithoutReadableToolName_FailsClosed()
    {
        // A malformed pre_tool_call context must not allow an unexamined call through.
        var malformed = new AgentContext(new JsonObject
        {
            ["spec"] = "agent-hooks/0.1",
            ["interception_point"] = "pre_tool_call",
            ["timestamp"] = "2026-08-09T00:00:00Z",
            ["sequence"] = 1,
            ["agent"] = new JsonObject { ["id"] = "a", ["framework"] = "agenteval" },
            ["session"] = new JsonObject { ["id"] = "s" },
            ["target"] = new JsonObject(),
            ["tool_call"] = new JsonObject { ["id"] = "tc-1" },   // no name
        });

        var interceptor = new GatekeeperInterceptor([new StubGate(ToolGateVerdict.Allow("A"))]);

        var verdict = await interceptor.InterceptAsync(malformed, CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.Contains("malformed_context", verdict.Reason);
    }

    [Fact]
    public async Task GateSeesToolNameAndArguments_FromTheSpecContext()
    {
        GatedToolCall? seen = null;
        var interceptor = new GatekeeperInterceptor([new CapturingGate(c => seen = c)]);

        await interceptor.InterceptAsync(
            PreToolCall("write_file", new JsonObject { ["path"] = "/etc/passwd", ["overwrite"] = true }),
            CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("write_file", seen!.FunctionName);
        Assert.Equal("/etc/passwd", seen.Arguments!["path"]);
        Assert.Equal(true, seen.Arguments["overwrite"]);
        Assert.Equal("TestAgent", seen.AgentName);
    }

    private sealed class CapturingGate(Action<GatedToolCall> capture) : IToolGate
    {
        public string PolicyName => "Capture";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
        {
            capture(call);
            return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
        }
    }

    [Theory]
    [InlineData("\"/etc/passwd\"", typeof(string))]
    [InlineData("true", typeof(bool))]
    [InlineData("42", typeof(long))]
    [InlineData("1.5", typeof(double))]
    public async Task ArgumentScalars_ArriveAsClrPrimitives_NotJsonNodes(string rawJson, Type expected)
    {
        // REGRESSION (S1). The first cut dispatched on JsonValue.TryGetValue<T>, which keys on the node's CLR
        // STORAGE type rather than its JSON kind. Values reached the gates as JsonNode instead of string/bool/
        // number, so every value-matching gate compared against a node reference, matched nothing, and the call
        // read as a clean allow — a silent fail-open. The predicate is now GetValueKind(), which is
        // construction-agnostic. These cases pin each JSON kind to its CLR type.
        GatedToolCall? seen = null;
        var interceptor = new GatekeeperInterceptor([new CapturingGate(c => seen = c)]);
        var args = new JsonObject { ["v"] = JsonNode.Parse(rawJson) };

        await interceptor.InterceptAsync(PreToolCall("t", args), CancellationToken.None);

        Assert.NotNull(seen?.Arguments);
        Assert.IsType(expected, seen!.Arguments!["v"]);
    }

    [Fact]
    public async Task ArgumentObjects_StayAsJsonNodes_SoSerializingGatesSeeFaithfulJson()
    {
        GatedToolCall? seen = null;
        var interceptor = new GatekeeperInterceptor([new CapturingGate(c => seen = c)]);
        var args = new JsonObject { ["nested"] = new JsonObject { ["k"] = "v" } };

        await interceptor.InterceptAsync(PreToolCall("t", args), CancellationToken.None);

        Assert.IsAssignableFrom<JsonNode>(seen!.Arguments!["nested"]);
    }

    [Fact]
    public void HasConversation_IsFalse_WhenContextOmitsMessages()
    {
        // Documents the fidelity limit: without `messages`, conversation-correlating gates
        // (ReferentialIntegrityGate, TaintTrackingGate) have nothing to correlate against, so their silence
        // is a coverage gap rather than a pass.
        Assert.False(AgentContextMapper.HasConversation(PreToolCall("read_file")));
    }
}
