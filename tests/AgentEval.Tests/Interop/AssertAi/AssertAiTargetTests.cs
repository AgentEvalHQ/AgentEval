// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Interop.AssertAi;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Interop.AssertAi;

/// <summary>
/// A .NET agent as an ASSERT HTTP endpoint target. <see cref="AssertPersists"/> ports what ASSERT does with an
/// endpoint's reply (assert-ai 0.3.0, <c>core/session.py</c> and <c>stages/inference.py</c>); it is checked against
/// ASSERT's own test of that code first, then used to show what ASSERT's judge sees of our replies.
/// </summary>
public class AssertAiTargetTests
{
    private static readonly string Fixtures = AssertAiRunTests.Fixtures;

    [Fact]
    public void ThePortOfAssertsReplyHandling_ReproducesAssertsOwnTest()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "http_endpoint_tool_events.json")))!;

        var persisted = AssertPersists((JsonObject)fixture["endpoint_response_body"]!, "user turn");

        Assert.Equal(fixture["expected_interaction_message_roles"]!.AsArray().Select(r => (string)r!), persisted.InteractionRoles);
        var expectedCall = fixture["expected_tool_call_message_tool_calls"]![0]!;
        Assert.Equal(((string)expectedCall["id"]!, (string)expectedCall["function"]!), (persisted.Aliases[0], persisted.ToolEdits[0].Name));
        Assert.True(JsonNode.DeepEquals(expectedCall["arguments"], persisted.ToolEdits[0].Args));
    }

    [Theory]
    [InlineData("request.prompt_case.json", 1)]
    [InlineData("request.scenario_turn3.json", 5)]
    [InlineData("request.sandbox_with_case_id.json", 1)]
    public void AssertsRequestBodies_BecomeTheConversation_WithTheCurrentTurnOnce(string file, int messages)
    {
        var body = File.ReadAllText(Path.Combine(Fixtures, file));
        var request = AssertAiTargetRequest.Parse(body);
        var conversation = new AssertAiTarget(new FixedClient("ok")).Conversation(request);

        Assert.Equal(messages, conversation.Count);
        Assert.Equal(ChatRole.User, conversation[^1].Role);
        Assert.Equal(request.Message, conversation[^1].Text);
        Assert.Equal(file.Contains("sandbox", StringComparison.Ordinal), request.CaseId is not null);
        if (file.Contains("prompt", StringComparison.Ordinal))
        {
            Assert.Equal("Restore line L1002 now — café ✓", request.Message);   // ASSERT sends it \u-escaped
        }
    }

    [Fact]
    public async Task AnAgentThatCallsATool_Replies_SoAssertsJudgeSeesTheCallItsArgumentsAndItsResult()
    {
        var target = new AssertAiTarget(ToolAgent(), new ChatOptions { Tools = [ResumeLine()] }, systemPrompt: "You restore lines.");

        var json = await target.RespondJsonAsync(File.ReadAllText(Path.Combine(Fixtures, "request.prompt_case.json")));
        var reply = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(["response", "events"], reply.Select(p => p.Key));
        Assert.Equal("The line is restored.", (string)reply["response"]!);
        var persisted = AssertPersists(reply, "Restore line L1002 now");
        var edit = Assert.Single(persisted.ToolEdits);
        Assert.Equal("resume_line", edit.Name);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"line_id":"L1002"}"""), edit.Args));
        Assert.Equal("""{"status":"resumed"}""", edit.Result);
        Assert.Equal(["user", "tool", "assistant"], persisted.InteractionRoles);   // no empty assistant message for the call
    }

    [Fact]
    public void ACallThatGotNoResult_IsStillSent_SoTheJudgeSeesItWasMade()
    {
        var produced = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "set_payout", new Dictionary<string, object?> { ["account"] = "X-99" })]),
        };

        var reply = AssertAiTarget.ToResponse(produced).ToJson();

        var edit = Assert.Single(AssertPersists(reply, "u").ToolEdits);
        Assert.Equal(("set_payout", ""), (edit.Name, edit.Result));
        Assert.Equal("X-99", (string)edit.Args!["account"]!);
    }

    [Fact]
    public void TextBeforeAToolCall_IsAnAssistantEvent_AndTheLastTextIsTheResponse()
    {
        var produced = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextContent("Let me check."), new FunctionCallContent("c1", "lookup", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", 42)]),
            new(ChatRole.Assistant, "It is 42."),
        };

        var response = AssertAiTarget.ToResponse(produced);

        Assert.Equal("It is 42.", response.Response);
        Assert.Equal([("assistant", "Let me check."), ("tool_result", "42")], response.Events.Select(e => (e.Role, e.Content)));
        Assert.Equal(["user", "assistant", "tool", "assistant"], AssertPersists(response.ToJson(), "u").InteractionRoles);
    }

    [Fact]
    public async Task TheServer_AnswersAssertsRequest_WithJson_AndRefusesWhatAssertWouldNotSend()
    {
        var port = FreePort();
        await using var server = AssertAiTargetServer.Start(new AssertAiTarget(ToolAgent(), new ChatOptions { Tools = [ResumeLine()] }), port, "/assert");
        using var http = new HttpClient();

        // The bytes ASSERT sends: Python json.dumps, non-ASCII \u-escaped.
        var ok = await http.PostAsync(server.Endpoint, new StringContent(File.ReadAllText(Path.Combine(Fixtures, "request.prompt_case.json")), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Matches(@"^application/(?:[\w.+-]+?\+)?json", ok.Content.Headers.ContentType!.ToString());   // ASSERT's (aiohttp's) check
        Assert.Equal("The line is restored.", (string)JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["response"]!);

        var bad = await http.PostAsync(server.Endpoint, new StringContent("[1,2]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.NotNull(JsonNode.Parse(await bad.Content.ReadAsStringAsync())!["error"]);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync(server.Endpoint)).StatusCode);

        var slash = await http.PostAsync(new Uri(server.Endpoint + "/"), new StringContent(File.ReadAllText(Path.Combine(Fixtures, "request.prompt_case.json")), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, slash.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(new Uri($"http://localhost:{port}/elsewhere"), new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task AnAgentThatFails_Answers500_WhichAssertRecordsAsATargetError()
    {
        var port = FreePort();
        await using var server = AssertAiTargetServer.Start(
            new AssertAiTarget((_, _) => throw new InvalidOperationException("model unavailable")), port);
        using var http = new HttpClient();

        var reply = await http.PostAsync(server.Endpoint, new StringContent("""{"message": "hi", "history": [{"role": "user", "content": "hi"}]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, reply.StatusCode);
        Assert.Contains("model unavailable", (string)JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["error"]!, StringComparison.Ordinal);
    }

    // ---- ASSERT's handling of an endpoint reply, ported -----------------------------------------------------------

    internal sealed record Persisted(List<string> InteractionRoles, List<string> Aliases, List<(string Name, JsonObject? Args, string Result)> ToolEdits);

    /// <summary>
    /// What ASSERT keeps of an endpoint reply for one prompt turn: <c>_normalize_connector_response</c> (roles other than
    /// assistant / tool_call / tool_result dropped, tool_args kept only as an object), id aliasing, the interaction
    /// messages (<c>session.py:1091-1148</c>), and what the inference stage persists (<c>inference.py:361-447</c>): a
    /// tool result becomes a tool-call edit with the name and arguments of its call, else its own.
    /// </summary>
    internal static Persisted AssertPersists(JsonObject reply, string userText)
    {
        var response = reply.ContainsKey("response")
            ? reply["response"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : reply["response"] is null ? "" : reply["response"]!.ToJsonString()
            : reply["text"]?.GetValue<string>() ?? "";
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Alias(JsonNode? id) => id is JsonValue iv && iv.TryGetValue<string>(out var sid)
            ? aliases.TryGetValue(sid, out var a) ? a : aliases[sid] = $"endpoint-action-{aliases.Count}"
            : null;

        var roles = new List<string> { "user" };
        var pending = new Dictionary<string, (string Name, JsonObject? Args)>(StringComparer.Ordinal);
        var edits = new List<(string, JsonObject?, string)>();
        var finalShown = false;
        foreach (var e in reply["events"] as JsonArray ?? [])
        {
            if (e is not JsonObject ev || (string?)ev["role"] is not ("assistant" or "tool_call" or "tool_result") || (string?)ev["role"] is not { } role)
            {
                continue;
            }

            var content = ev["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : "";
            var name = ev["tool_name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && n.Length > 0 ? n : "tool";
            var args = ev["tool_args"] as JsonObject;
            var alias = Alias(ev["tool_call_id"]);
            switch (role)
            {
                case "assistant":
                    roles.Add("assistant");
                    finalShown |= content == response;
                    break;
                case "tool_call":
                    roles.Add("assistant");
                    if (alias is not null) pending[alias] = (name, args);
                    break;
                default:
                    roles.Add("tool");
                    var (callName, callArgs) = alias is not null && pending.TryGetValue(alias, out var p) ? p : (name, args ?? new JsonObject());
                    edits.Add((callName, callArgs, content));
                    break;
            }
        }

        if (!finalShown)
        {
            roles.Add("assistant");
        }

        _ = userText;
        return new Persisted(roles, aliases.Values.ToList(), edits);
    }

    // ---- fakes ---------------------------------------------------------------------------------------------

    private static AIFunction ResumeLine() => AIFunctionFactory.Create((string line_id) => """{"status":"resumed"}""", "resume_line", "Resume a suspended line.");

    private static IChatClient ToolAgent() => new ChatClientBuilder(new ToolCallingClient()).UseFunctionInvocation().Build();

    /// <summary>Calls resume_line once, then answers.</summary>
    private sealed class ToolCallingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var called = messages.Any(m => m.Role == ChatRole.Tool);
            return Task.FromResult(called
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "The line is restored."))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "resume_line", new Dictionary<string, object?> { ["line_id"] = "L1002" })])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FixedClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
