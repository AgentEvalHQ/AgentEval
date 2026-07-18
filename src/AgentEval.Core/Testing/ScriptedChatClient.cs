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

    /// <summary>
    /// Gatekeeper Hardening Phase 2 fixture prerequisite — scripts an assistant turn that emits MULTIPLE tool
    /// calls in ONE turn (finish reason "tool_calls"), the shape a real provider sends for parallel function
    /// calling. Before this, <see cref="ScriptedChatClient"/> could only ever emit one
    /// <see cref="FunctionCallContent"/> per turn, so no test could exercise a gate pipeline against MAF's
    /// FunctionInvokingChatClient actually invoking N calls from a single assistant message (sequential or
    /// concurrent under <c>AllowConcurrentInvocation</c>) — only against N single-call turns in a row, which is
    /// a materially different code path (FICC does not batch those).
    /// </summary>
    public ScriptedChatClient AddParallelToolCalls(params (string CallId, string Name, IDictionary<string, object?> Args)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Length == 0)
        {
            throw new ArgumentException("At least one tool call is required.", nameof(calls));
        }

        return Add(new ScriptedTurn
        {
            ToolCalls = calls
                .Select(c => new ScriptedToolCall { ToolCallId = c.CallId, ToolName = c.Name, ToolArgs = c.Args })
                .ToList(),
            FinishReason = ChatFinishReason.ToolCalls,
        });
    }

    /// <summary>Scripts a turn that throws (simulates a transient/provider error).</summary>
    public ScriptedChatClient AddThrow(string message = "Simulated provider error")
        => Add(new ScriptedTurn { Throw = message });

    /// <summary>
    /// Loads a script from data — the counterpart to every <c>Add*</c> method above, which all build the
    /// queue by hand in C#. Used by <c>agenteval log-file to-fixture</c> (CLI, <c>AgentEval.Cli</c>) to hydrate
    /// a <see cref="ScriptedChatClient"/> from a captured real conversation
    /// (<c>--capture-fixture</c> → <see cref="ScriptedFixtureTurn"/>[] JSON), so a real, once-observed
    /// conversation becomes a deterministic, versionable test fixture. A fresh <see cref="ScriptedChatClient"/>
    /// is returned (this is a factory, not an instance method) — every existing usage pattern
    /// (<c>new ScriptedChatClient().AddText(...)</c>) still works unchanged; this is purely additive.
    /// </summary>
    public static ScriptedChatClient FromFixture(IEnumerable<ScriptedFixtureTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var client = new ScriptedChatClient();
        foreach (var turn in turns)
        {
            client.Add(turn.ToScriptedTurn());
        }

        return client;
    }

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
        if (turn.ToolCalls is { Count: > 0 })
        {
            // Parallel-call fixture path: emit every scripted call as its own FunctionCallContent in the SAME
            // assistant message — this is what makes FICC treat them as N calls from one turn (FunctionCount > 1
            // in FunctionInvocationContext), not N separate single-call turns.
            var i = 0;
            foreach (var call in turn.ToolCalls)
            {
                contents.Add(new FunctionCallContent(call.ToolCallId ?? $"call_{i}", call.ToolName, call.ToolArgs ?? new Dictionary<string, object?>()));
                i++;
            }
        }
        else if (turn.ToolName is not null)
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

    /// <summary>
    /// Multiple tool calls to emit in ONE turn (parallel function calling) — set via
    /// <see cref="ScriptedChatClient.AddParallelToolCalls"/>. Takes precedence over the single
    /// <see cref="ToolCallId"/>/<see cref="ToolName"/>/<see cref="ToolArgs"/> trio above when non-empty; the
    /// single-call trio remains the simple path for the common one-call-per-turn case.
    /// </summary>
    public IReadOnlyList<ScriptedToolCall>? ToolCalls { get; init; }

    /// <summary>Finish reason for the turn (e.g. <see cref="ChatFinishReason.Stop"/> / <see cref="ChatFinishReason.ToolCalls"/>).</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>Prompt token count to report (optional).</summary>
    public long? InputTokens { get; init; }

    /// <summary>Completion token count to report (optional).</summary>
    public long? OutputTokens { get; init; }

    /// <summary>When set, the turn throws an <see cref="InvalidOperationException"/> with this message.</summary>
    public string? Throw { get; init; }
}

/// <summary>One tool call within a parallel-tool-call <see cref="ScriptedTurn"/> — see <see cref="ScriptedChatClient.AddParallelToolCalls"/>.</summary>
public sealed class ScriptedToolCall
{
    /// <summary>Tool-call id; defaults to <c>call_{index}</c> within the turn when null.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool-call arguments (defaults to empty when null).</summary>
    public IDictionary<string, object?>? ToolArgs { get; init; }
}

/// <summary>
/// A 1:1 serializable mirror of <see cref="ScriptedTurn"/>, for <see cref="ScriptedChatClient.FromFixture"/>.
/// Exists as a SEPARATE type rather than making <see cref="ScriptedTurn"/> itself serializable because
/// <see cref="ScriptedTurn.FinishReason"/> is a <see cref="ChatFinishReason"/> (a MEAI extensible-enum struct)
/// — this type stores it as a plain <see cref="string"/> instead, so a fixture JSON file has no dependency on
/// how that struct happens to (de)serialize.
/// </summary>
public sealed class ScriptedFixtureTurn
{
    /// <summary>See <see cref="ScriptedTurn.Text"/>.</summary>
    public string? Text { get; init; }

    /// <summary>See <see cref="ScriptedTurn.ToolCallId"/>.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>See <see cref="ScriptedTurn.ToolName"/>.</summary>
    public string? ToolName { get; init; }

    /// <summary>See <see cref="ScriptedTurn.ToolArgs"/>.</summary>
    public IDictionary<string, object?>? ToolArgs { get; init; }

    /// <summary>See <see cref="ScriptedTurn.ToolCalls"/>.</summary>
    public IReadOnlyList<ScriptedFixtureToolCall>? ToolCalls { get; init; }

    /// <summary>See <see cref="ScriptedTurn.FinishReason"/> — stored as its raw <see cref="ChatFinishReason.Value"/>.</summary>
    public string? FinishReason { get; init; }

    /// <summary>See <see cref="ScriptedTurn.InputTokens"/>.</summary>
    public long? InputTokens { get; init; }

    /// <summary>See <see cref="ScriptedTurn.OutputTokens"/>.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>See <see cref="ScriptedTurn.Throw"/>.</summary>
    public string? Throw { get; init; }

    /// <summary>Converts this data-only shape into the real <see cref="ScriptedTurn"/> <see cref="ScriptedChatClient"/> consumes.</summary>
    public ScriptedTurn ToScriptedTurn() => new()
    {
        Text = Text,
        ToolCallId = ToolCallId,
        ToolName = ToolName,
        ToolArgs = ToolArgs,
        ToolCalls = ToolCalls?.Select(c => new ScriptedToolCall { ToolCallId = c.ToolCallId, ToolName = c.ToolName, ToolArgs = c.ToolArgs }).ToList(),
        FinishReason = FinishReason is null ? null : new ChatFinishReason(FinishReason),
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        Throw = Throw,
    };
}

/// <summary>A 1:1 serializable mirror of <see cref="ScriptedToolCall"/> — see <see cref="ScriptedFixtureTurn"/>.</summary>
public sealed class ScriptedFixtureToolCall
{
    /// <summary>See <see cref="ScriptedToolCall.ToolCallId"/>.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>See <see cref="ScriptedToolCall.ToolName"/>.</summary>
    public required string ToolName { get; init; }

    /// <summary>See <see cref="ScriptedToolCall.ToolArgs"/>.</summary>
    public IDictionary<string, object?>? ToolArgs { get; init; }
}
