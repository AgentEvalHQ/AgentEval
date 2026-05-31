// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Testing;

/// <summary>
/// Deterministic <see cref="IChatClient"/> for Glass Box tests. Unlike <c>FakeChatClient</c> it can script
/// tool calls (<see cref="FunctionCallContent"/>), finish reasons, token usage, streaming, and a throw —
/// the inputs chat-boundary / Trace Fidelity tests need. Each scripted turn is consumed in order; once the
/// queue is empty, a benign empty "stop" response is returned.
/// </summary>
/// <remarks>
/// This is a hand-rolled fake rather than a Moq mock because Moq cannot cleanly script
/// <c>IAsyncEnumerable&lt;ChatResponseUpdate&gt;</c> streaming plus <see cref="AIContent"/> payloads. For simple
/// collaborator interfaces, prefer Moq; for <see cref="IChatClient"/>, use this.
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ScriptedTurn> _turns = new();

    /// <summary>Every message list received, in call order (full history per call).</summary>
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new();

    /// <summary>Every <see cref="ChatOptions"/> received, in call order (may contain nulls).</summary>
    public List<ChatOptions?> ReceivedOptions { get; } = new();

    /// <summary>Number of calls made (non-streaming and streaming both route through here).</summary>
    public int CallCount => ReceivedMessages.Count;

    /// <summary>Enqueues a scripted turn.</summary>
    public ScriptedChatClient Add(ScriptedTurn turn)
    {
        _turns.Enqueue(turn);
        return this;
    }

    /// <summary>Scripts a plain assistant text reply with finish reason "stop".</summary>
    public ScriptedChatClient AddText(string text, long? inTok = null, long? outTok = null)
        => Add(new ScriptedTurn { Text = text, FinishReason = ChatFinishReason.Stop, InputTokens = inTok, OutputTokens = outTok });

    /// <summary>Scripts an assistant turn that emits a tool call (finish reason "tool_calls").</summary>
    public ScriptedChatClient AddToolCall(string callId, string name, IDictionary<string, object?> args)
        => Add(new ScriptedTurn { ToolCallId = callId, ToolName = name, ToolArgs = args, FinishReason = ChatFinishReason.ToolCalls });

    /// <summary>Scripts a turn that throws (simulates a transient/provider error).</summary>
    public ScriptedChatClient AddThrow(string message = "Simulated provider error")
        => Add(new ScriptedTurn { Throw = message });

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add(messages.ToList());
        ReceivedOptions.Add(options);

        var turn = _turns.Count > 0 ? _turns.Dequeue() : new ScriptedTurn { Text = string.Empty, FinishReason = ChatFinishReason.Stop };
        if (turn.Throw is not null)
        {
            throw new InvalidOperationException(turn.Throw);
        }

        var contents = new List<AIContent>();
        if (turn.ToolName is not null)
        {
            contents.Add(new FunctionCallContent(turn.ToolCallId ?? "call_0", turn.ToolName, turn.ToolArgs ?? new Dictionary<string, object?>()));
        }

        if (!string.IsNullOrEmpty(turn.Text))
        {
            contents.Add(new TextContent(turn.Text));
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);
        var response = new ChatResponse(message) { FinishReason = turn.FinishReason, ModelId = "scripted" };
        if (turn.InputTokens is not null || turn.OutputTokens is not null)
        {
            response.Usage = new UsageDetails { InputTokenCount = turn.InputTokens, OutputTokenCount = turn.OutputTokens };
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                FinishReason = response.FinishReason,
                ModelId = response.ModelId,
            };
        }

        // Real providers surface token usage in a trailing streamed chunk (as a UsageContent), not on the
        // text updates — mirror that so streaming recorders can be exercised for usage capture.
        if (response.Usage is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(response.Usage) })
            {
                ModelId = response.ModelId,
            };
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose.
    }
}

/// <summary>One scripted model turn for <see cref="ScriptedChatClient"/>.</summary>
public sealed class ScriptedTurn
{
    /// <summary>Assistant text content (optional).</summary>
    public string? Text { get; init; }

    /// <summary>Tool-call id when this turn emits a tool call.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name when this turn emits a tool call; null for a text-only turn.</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool-call arguments (defaults to empty when null).</summary>
    public IDictionary<string, object?>? ToolArgs { get; init; }

    /// <summary>Finish reason for the turn (e.g. <see cref="ChatFinishReason.Stop"/> / <see cref="ChatFinishReason.ToolCalls"/>).</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>Prompt token count to report (optional).</summary>
    public long? InputTokens { get; init; }

    /// <summary>Completion token count to report (optional).</summary>
    public long? OutputTokens { get; init; }

    /// <summary>When set, the turn throws an <see cref="InvalidOperationException"/> with this message.</summary>
    public string? Throw { get; init; }
}
