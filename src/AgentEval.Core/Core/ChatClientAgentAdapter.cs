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
public class ChatClientAgentAdapter : IStreamableAgent, ISessionResettableAgent
{
    private readonly IChatClient _chatClient;
    private readonly ChatOptions? _chatOptions;
    private readonly string _systemPrompt;
    private readonly bool _includeHistory;
    private readonly List<ChatMessage> _conversationHistory;

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

        var result = await _chatClient.GetResponseAsync(messages, _chatOptions, cancellationToken);

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

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, _chatOptions, cancellationToken))
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

        // Add conversation history if maintaining state
        if (_includeHistory)
        {
            messages.AddRange(_conversationHistory);
        }

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
