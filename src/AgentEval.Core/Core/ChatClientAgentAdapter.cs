// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Adapter that wraps an IChatClient as an IStreamableAgent for testing.
/// This enables using Microsoft.Extensions.AI chat clients directly with AgentEval.
/// </summary>
public class ChatClientAgentAdapter
    : IStreamableAgent, ISessionResettableAgent, IHistoryInjectableAgent,
      ITimestampedHistoryInjectableAgent, IAnswerSamplingConfigurableAgent
{
    /// <summary>How an injected timestamp is rendered into a message: <c>[2026/03/03 09:12] </c>.</summary>
    public const string TimestampFormat = "yyyy/MM/dd HH:mm";

    private readonly IChatClient _chatClient;
    private readonly ChatOptions? _chatOptions;
    private readonly string _systemPrompt;
    private readonly bool _includeHistory;
    private readonly List<ChatMessage> _conversationHistory;
    private double? _answerTemperature;
    private int? _answerSeed;
    private DateTimeOffset? _queryTime;

    /// <summary>
    /// Creates a new adapter wrapping an IChatClient.
    /// </summary>
    /// <param name="chatClient">The chat client to wrap.</param>
    /// <param name="name">Name for this agent instance.</param>
    /// <param name="systemPrompt">Optional system prompt to include with each request.</param>
    /// <param name="chatOptions">Optional chat options for requests.</param>
    /// <param name="includeHistory">Whether to maintain conversation history across calls.</param>
    public ChatClientAgentAdapter(
        IChatClient chatClient,
        string name = "ChatClientAgent",
        string? systemPrompt = null,
        ChatOptions? chatOptions = null,
        bool includeHistory = false)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Name = name;
        _systemPrompt = systemPrompt ?? string.Empty;
        _chatOptions = chatOptions;
        _includeHistory = includeHistory;
        _conversationHistory = new List<ChatMessage>();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(prompt);

        var result = await _chatClient.GetResponseAsync(messages, EffectiveChatOptions(), cancellationToken);

        if (_includeHistory)
        {
            _conversationHistory.Add(new ChatMessage(ChatRole.User, prompt));
            // Add the last message from the response
            var lastMessage = result.Messages.LastOrDefault();
            if (lastMessage != null)
            {
                _conversationHistory.Add(lastMessage);
            }
        }

        return new AgentResponse
        {
            Text = result.Text ?? string.Empty,
            ModelId = result.ModelId,
            TokenUsage = ConvertTokenUsage(result.Usage),
            RawMessages = result.Messages.Cast<object>().ToList()
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(prompt);
        var textBuilder = new StringBuilder();
        TokenUsage? capturedUsage = null;

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, EffectiveChatOptions(), cancellationToken))
        {
            // Handle text content
            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuilder.Append(update.Text);
                yield return new AgentResponseChunk
                {
                    Text = update.Text,
                    IsComplete = false
                };
            }
            
            // Check for structured content in the streaming update
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usage)
                    {
                        capturedUsage = new TokenUsage
                        {
                            PromptTokens = (int)(usage.Details.InputTokenCount ?? 0),
                            CompletionTokens = (int)(usage.Details.OutputTokenCount ?? 0)
                        };
                    }
                    else if (content is FunctionCallContent call)
                    {
                        yield return new AgentResponseChunk
                        {
                            ToolCallStarted = new ToolCallInfo
                            {
                                Name = call.Name,
                                CallId = call.CallId,
                                Arguments = call.Arguments
                            }
                        };
                    }
                    else if (content is FunctionResultContent result)
                    {
                        yield return new AgentResponseChunk
                        {
                            ToolCallCompleted = new ToolResultInfo
                            {
                                CallId = result.CallId,
                                Result = result.Result,
                                Exception = result.Exception
                            }
                        };
                    }
                }
            }
        }

        // Final chunk with complete flag
        if (_includeHistory)
        {
            _conversationHistory.Add(new ChatMessage(ChatRole.User, prompt));
            _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, textBuilder.ToString()));
        }

        yield return new AgentResponseChunk
        {
            IsComplete = true,
            Usage = capturedUsage
        };
    }

    /// <summary>
    /// Clears the conversation history.
    /// </summary>
    public void ClearHistory()
    {
        _conversationHistory.Clear();
        _queryTime = null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's <see cref="ChatOptions"/> instance is never mutated: an effective copy is built
    /// per call, so an adapter shared between runs cannot leak one run's sampling into another's.
    /// </remarks>
    public AnswerSamplingAcknowledgement ConfigureAnswerSampling(AnswerSamplingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _answerTemperature = request.Temperature;
        _answerSeed = request.Seed;
        return AnswerSamplingAcknowledgement.AppliedFrom(request);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This adapter has no store, so the only way it can honour a timestamp is to put it where its
    /// model can read it: each turn is prefixed with its instant, and the query time is stated in a
    /// system message. That makes the adapter a system which places messages in time perfectly, by
    /// construction — useful as a ceiling, and not a substitute for testing a real memory system.
    /// </remarks>
    public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        foreach (var turn in history.Turns)
        {
            _conversationHistory.Add(new ChatMessage(ChatRole.User, Stamp(turn.Timestamp, turn.UserMessage)));
            _conversationHistory.Add(
                new ChatMessage(ChatRole.Assistant, Stamp(turn.Timestamp, turn.AssistantResponse)));
        }
        _queryTime = history.QueryTime;
    }

    /// <summary>Renders an instant onto a message, as <c>[2026/03/03 09:12] text</c>.</summary>
    public static string Stamp(DateTimeOffset timestamp, string text)
        => $"[{timestamp.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture)}] {text}";

    /// <summary>States the query time as a system message, for a time-grounded run.</summary>
    public static string QueryTimeSystemMessage(DateTimeOffset queryTime)
        => $"Current date and time: " +
           $"{queryTime.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture)} UTC.";

    /// <summary>
    /// The options actually sent: the caller's, plus any evaluator-requested sampling. Returns the
    /// caller's instance untouched when no sampling was requested, so a default run sends exactly
    /// what it always sent.
    /// </summary>
    private ChatOptions? EffectiveChatOptions()
    {
        if (_answerTemperature is null && _answerSeed is null)
            return _chatOptions;

        var options = _chatOptions?.Clone() ?? new ChatOptions();
        if (_answerTemperature is { } temperature)
            options.Temperature = (float)temperature;
        if (_answerSeed is { } seed)
            options.Seed = seed;
        return options;
    }

    /// <inheritdoc />
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        ClearHistory();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current conversation history.
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _conversationHistory.AsReadOnly();

    /// <inheritdoc />
    public void InjectConversationHistory(IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        foreach (var (userMessage, assistantResponse) in conversationTurns)
        {
            _conversationHistory.Add(new ChatMessage(ChatRole.User, userMessage));
            _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));
        }
    }

    /// <summary>
    /// Creates a ChatClientAgentAdapter with a specific model configuration.
    /// </summary>
    public static ChatClientAgentAdapter Create(
        IChatClient chatClient,
        string? name = null,
        string? systemPrompt = null,
        float? temperature = null,
        int? maxTokens = null)
    {
        var options = new ChatOptions();
        if (temperature.HasValue)
            options.Temperature = temperature;
        if (maxTokens.HasValue)
            options.MaxOutputTokens = maxTokens;

        return new ChatClientAgentAdapter(
            chatClient,
            name ?? "ChatClientAgent",
            systemPrompt,
            options);
    }

    private List<ChatMessage> BuildMessages(string prompt)
    {
        var messages = new List<ChatMessage>();

        // Add system prompt if configured
        if (!string.IsNullOrEmpty(_systemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        }

        // Only set by a time-grounded injection: the "now" that decides already-happened from
        // not-yet-happened, which no amount of conversation history supplies on its own.
        if (_queryTime is { } queryTime)
        {
            messages.Add(new ChatMessage(ChatRole.System, QueryTimeSystemMessage(queryTime)));
        }

        // Always include history — populated by conversation tracking (_includeHistory=true)
        // or by InjectConversationHistory(), which must work regardless of _includeHistory.
        messages.AddRange(_conversationHistory);

        // Add the current user prompt
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        return messages;
    }

    private static TokenUsage? ConvertTokenUsage(UsageDetails? usage)
    {
        if (usage == null)
            return null;

        return new TokenUsage
        {
            PromptTokens = (int)(usage.InputTokenCount ?? 0),
            CompletionTokens = (int)(usage.OutputTokenCount ?? 0)
        };
    }
}
