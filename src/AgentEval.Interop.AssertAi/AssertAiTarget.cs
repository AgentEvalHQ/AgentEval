// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentEval.Interop.AssertAi;

/// <summary>One message of the conversation ASSERT sends (<c>history[i]</c>): <c>user</c> or <c>assistant</c>.</summary>
public sealed record AssertAiTargetMessage(string Role, string Content);

/// <summary>
/// The body ASSERT POSTs to an HTTP endpoint target (assert-ai 0.3.0, <c>core/session.py</c>):
/// <c>{"message": …, "history": [{"role", "content"}, …]}</c>. <c>history</c> holds every user and assistant message
/// so far, oldest first, and already ends with the current user message. ASSERT never sends its system prompt, tool
/// messages or tool calls; <c>case_id</c> is sent only by its sandbox targets.
/// </summary>
/// <param name="Message">The latest user message.</param>
/// <param name="History">The conversation so far, including <paramref name="Message"/> as its last entry.</param>
/// <param name="CaseId">The test case id, when ASSERT sends it.</param>
public sealed record AssertAiTargetRequest(string Message, IReadOnlyList<AssertAiTargetMessage> History, string? CaseId = null)
{
    /// <summary>Reads a request body.</summary>
    /// <exception cref="InvalidDataException">The body is not ASSERT's request shape.</exception>
    public static AssertAiTargetRequest Parse(string json)
    {
        if (AssertAiJson.ParsePython(json, "request", null) is not JsonObject body)
        {
            throw new InvalidDataException("The request body must be a JSON object.");
        }

        var history = new List<AssertAiTargetMessage>();
        if (body["history"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is JsonObject m && AssertAiJson.Str(m["role"]) is { } role)
                {
                    history.Add(new AssertAiTargetMessage(role, AssertAiJson.Str(m["content"]) ?? string.Empty));
                }
            }
        }
        else if (body["history"] is not null)
        {
            throw new InvalidDataException("history must be a list of {role, content} objects.");
        }

        var message = AssertAiJson.Str(body["message"]);
        if (message is null && history.Count == 0)
        {
            throw new InvalidDataException("The request has neither a message nor a history.");
        }

        return new AssertAiTargetRequest(message ?? string.Empty, history, AssertAiJson.Str(body["case_id"]));
    }
}

/// <summary>One event of a target's reply: a tool result (with the call's name and arguments) or an assistant message.</summary>
/// <param name="Role"><c>tool_result</c>, <c>tool_call</c> or <c>assistant</c>.</param>
/// <param name="Content">The tool's output, or the message text.</param>
/// <param name="ToolName">The tool called.</param>
/// <param name="ToolArgs">The call's arguments (ASSERT keeps them only when they are a JSON object).</param>
/// <param name="ToolCallId">The call id (ASSERT renames it and does not keep it).</param>
public sealed record AssertAiTargetEvent(string Role, string Content, string? ToolName = null, JsonObject? ToolArgs = null, string? ToolCallId = null);

/// <summary>A target's reply in the shape ASSERT reads: <c>{"response": …, "events": […]}</c>.</summary>
/// <param name="Response">The final answer.</param>
/// <param name="Events">What happened on the way, in order.</param>
public sealed record AssertAiTargetResponse(string Response, IReadOnlyList<AssertAiTargetEvent> Events)
{
    /// <summary>The reply as JSON.</summary>
    public JsonObject ToJson()
    {
        var events = new JsonArray();
        foreach (var e in Events)
        {
            var item = new JsonObject { ["role"] = e.Role, ["content"] = e.Content };
            if (e.ToolName is not null) item["tool_name"] = e.ToolName;
            if (e.ToolArgs is not null) item["tool_args"] = e.ToolArgs.DeepClone();
            if (e.ToolCallId is not null) item["tool_call_id"] = e.ToolCallId;
            events.Add(item);
        }

        return new JsonObject { ["response"] = Response, ["events"] = events };
    }

    /// <summary>The reply as the JSON text an HTTP endpoint returns (<c>Content-Type: application/json</c>).</summary>
    public string ToJsonString() => ToJson().ToJsonString(AssertAiJson.Write);
}

/// <summary>
/// Serves a .NET chat client or agent as an ASSERT HTTP endpoint target: give it ASSERT's request, it runs the
/// conversation and returns ASSERT's reply, tool calls included. Host it in any web framework
/// (<see cref="RespondJsonAsync"/> takes and returns the bodies) or with <see cref="AssertAiTargetServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tool calls.</b> ASSERT's judge sees a tool call only through a <c>tool_result</c> event: a <c>tool_call</c> event
/// with no result adds an empty assistant message and nothing else. So every call is sent as a <c>tool_result</c>
/// carrying its name and arguments, as ASSERT's own reference endpoint does, and a call that got no result (a client
/// without automatic function invocation) is sent in its place with the content <c>(no result: the call was not run)</c>,
/// so the judge sees that it was made and does not read it as run.
/// </para>
/// <para>
/// <b>Text.</b> The last assistant text is <c>response</c>; earlier assistant texts are <c>assistant</c> events.
/// </para>
/// </remarks>
public sealed class AssertAiTarget
{
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>> _respond;
    private readonly string? _systemPrompt;

    /// <summary>A target over a chat client. Add <c>UseFunctionInvocation()</c> to the client for its tools to run.</summary>
    /// <param name="client">The model or agent client.</param>
    /// <param name="options">Options for every call (tools, temperature…).</param>
    /// <param name="systemPrompt">The target's own system prompt. ASSERT never sends one to an endpoint.</param>
    public AssertAiTarget(IChatClient client, ChatOptions? options = null, string? systemPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _systemPrompt = systemPrompt;
        _respond = async (messages, ct) =>
            (await client.GetResponseAsync(messages, options?.Clone(), ct).ConfigureAwait(false)).Messages.ToList();
    }

    /// <summary>
    /// A target over any agent: <paramref name="respond"/> receives the conversation and returns the messages it added
    /// (assistant text, function calls, function results). For a Microsoft Agent Framework agent:
    /// <c>async (messages, ct) =&gt; (await agent.RunAsync(messages, cancellationToken: ct)).Messages.ToList()</c>.
    /// </summary>
    /// <param name="respond">Runs one turn.</param>
    /// <param name="systemPrompt">A system prompt to put first, when the agent does not carry its own.</param>
    public AssertAiTarget(Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>> respond, string? systemPrompt = null)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
        _systemPrompt = systemPrompt;
    }

    /// <summary>Runs one turn of ASSERT's conversation.</summary>
    public async Task<AssertAiTargetResponse> RespondAsync(AssertAiTargetRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var produced = await _respond(Conversation(request), ct).ConfigureAwait(false);
        return ToResponse(produced);
    }

    /// <summary>Runs one turn from ASSERT's JSON request body and returns the JSON reply body.</summary>
    /// <exception cref="InvalidDataException">The body is not ASSERT's request shape (answer 400).</exception>
    public async Task<string> RespondJsonAsync(string requestJson, CancellationToken ct = default) =>
        (await RespondAsync(AssertAiTargetRequest.Parse(requestJson), ct).ConfigureAwait(false)).ToJsonString();

    /// <summary>The conversation the agent sees: its system prompt, then ASSERT's history.</summary>
    internal IReadOnlyList<ChatMessage> Conversation(AssertAiTargetRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(_systemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        }

        foreach (var m in request.History)
        {
            if (m.Role is "user" or "assistant")
            {
                messages.Add(new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content));
            }
        }

        // ASSERT's history already ends with the current message; another caller's might not.
        var last = request.History.Count > 0 ? request.History[^1] : null;
        if (request.Message.Length > 0 && (last is null || last.Role != "user" || last.Content != request.Message))
        {
            messages.Add(new ChatMessage(ChatRole.User, request.Message));
        }

        return messages;
    }

    /// <summary>Turns the messages an agent added into ASSERT's reply.</summary>
    /// <param name="produced">The assistant and tool messages of one turn, in order.</param>
    public static AssertAiTargetResponse ToResponse(IReadOnlyList<ChatMessage> produced)
    {
        ArgumentNullException.ThrowIfNull(produced);
        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var answered = produced.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var events = new List<AssertAiTargetEvent>();
        var texts = new List<(int EventIndex, string Text)>();

        foreach (var message in produced)
        {
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
            if (message.Role == ChatRole.Assistant && text.Length > 0)
            {
                texts.Add((events.Count, text));
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call when !answered.Contains(call.CallId):
                        events.Add(new AssertAiTargetEvent("tool_result", AssertAiJudgeKit.NoResult, call.Name, Arguments(call), call.CallId));
                        break;
                    case FunctionCallContent call:
                        calls[call.CallId] = call;
                        break;
                    case FunctionResultContent result:
                        calls.TryGetValue(result.CallId, out var call2);
                        events.Add(new AssertAiTargetEvent("tool_result", ResultText(result), call2?.Name ?? "tool", Arguments(call2), result.CallId));
                        break;
                }
            }
        }

        var response = texts.Count > 0 ? texts[^1].Text : string.Empty;
        for (var i = texts.Count - 2; i >= 0; i--)
        {
            events.Insert(texts[i].EventIndex, new AssertAiTargetEvent("assistant", texts[i].Text));
        }

        return new AssertAiTargetResponse(response, events);
    }

    private static JsonObject? Arguments(FunctionCallContent? call)
    {
        if (call?.Arguments is not { } args)
        {
            return call is null ? null : new JsonObject();
        }

        return JsonSerializer.SerializeToNode(args, AIJsonUtilities.DefaultOptions) as JsonObject ?? new JsonObject();
    }

    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null when result.Exception is { } ex => $"Error: {ex.Message}",
        null => string.Empty,
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText(),
        JsonNode n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n.ToJsonString(AssertAiJson.Write),
        var other => JsonSerializer.Serialize(other, AIJsonUtilities.DefaultOptions),
    };
}
