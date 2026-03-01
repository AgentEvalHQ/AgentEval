// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

// Aliases to disambiguate SK vs M.E.AI types (both define FunctionCallContent, etc.)
using SKFunctionCall = Microsoft.SemanticKernel.FunctionCallContent;
using SKFunctionResult = Microsoft.SemanticKernel.FunctionResultContent;
using SKTextContent = Microsoft.SemanticKernel.TextContent;
using MEAIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAIChatRole = Microsoft.Extensions.AI.ChatRole;
using MEAIFunctionCall = Microsoft.Extensions.AI.FunctionCallContent;
using MEAIFunctionResult = Microsoft.Extensions.AI.FunctionResultContent;
using MEAITextContent = Microsoft.Extensions.AI.TextContent;
using MEAIContent = Microsoft.Extensions.AI.AIContent;

namespace AgentEval.NuGetConsumer.Adapters;

/// <summary>
/// Adapts a Semantic Kernel <see cref="ChatCompletionAgent"/> to AgentEval's
/// <see cref="IEvaluableAgent"/> — enabling the harness to run evaluation,
/// extract tool usage, track performance, and populate <see cref="AgentEval.Models.TestResult"/>
/// automatically.
///
/// Internally converts SK's <see cref="ChatMessageContent"/> messages to M.E.AI
/// <see cref="MEAIChatMessage"/> format so that <see cref="ToolUsageExtractor"/>
/// works seamlessly.
/// </summary>
/// <remarks>
/// This adapter lives in the NuGetConsumer sample for now. Once stable,
/// it should be promoted to a proper AgentEval.SemanticKernel library project.
/// </remarks>
public class SKAgentAdapter : IEvaluableAgent
{
    private readonly ChatCompletionAgent _agent;
    private ChatHistoryAgentThread? _thread;

    /// <summary>
    /// Wraps a Semantic Kernel ChatCompletionAgent for AgentEval evaluation.
    /// </summary>
    /// <param name="agent">The SK agent to evaluate.</param>
    /// <param name="thread">Optional existing thread. If null, a new one is created on first invocation.</param>
    public SKAgentAdapter(ChatCompletionAgent agent, ChatHistoryAgentThread? thread = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _thread = thread;
    }

    /// <inheritdoc />
    public string Name => _agent.Name ?? "SKAgent";

    /// <inheritdoc />
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _thread ??= new ChatHistoryAgentThread();

        // Collect ALL messages: intermediate (tool calls/results) + final response
        var skMessages = new List<ChatMessageContent>();

        await foreach (var response in _agent.InvokeAsync(
            prompt, _thread,
            options: new()
            {
                OnIntermediateMessage = msg =>
                {
                    skMessages.Add(msg);
                    return Task.CompletedTask;
                }
            },
            cancellationToken))
        {
            skMessages.Add(response.Message);
        }

        // Extract response text from assistant messages
        var text = string.Join("", skMessages
            .Where(m => m.Role == AuthorRole.Assistant && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content));

        // Convert SK messages → M.E.AI ChatMessages so ToolUsageExtractor works
        var rawMessages = ConvertToMEAIMessages(skMessages);

        return new AgentResponse
        {
            Text = text,
            RawMessages = rawMessages
        };
    }

    /// <summary>
    /// Resets the conversation thread for a fresh evaluation run.
    /// </summary>
    public void ResetThread()
    {
        _thread = null;
    }

    // ─── SK → M.E.AI message conversion ─────────────────────────────

    private static List<object> ConvertToMEAIMessages(List<ChatMessageContent> skMessages)
    {
        var result = new List<object>();

        foreach (var skMsg in skMessages)
        {
            var contents = new List<MEAIContent>();

            foreach (var item in skMsg.Items)
            {
                switch (item)
                {
                    case SKFunctionCall call:
                        // SK Arguments is IReadOnlyDictionary; MEAI needs IDictionary
                        var args = call.Arguments?.ToDictionary(
                            kvp => kvp.Key, kvp => (object?)kvp.Value);
                        contents.Add(new MEAIFunctionCall(
                            call.Id ?? $"call_{contents.Count}",
                            call.FunctionName,
                            args));
                        break;

                    case SKFunctionResult funcResult:
                        contents.Add(new MEAIFunctionResult(
                            funcResult.CallId ?? "",
                            funcResult.Result));
                        break;

                    case SKTextContent text when !string.IsNullOrEmpty(text.Text):
                        contents.Add(new MEAITextContent(text.Text));
                        break;
                }
            }

            if (contents.Count > 0)
            {
                var role = skMsg.Role == AuthorRole.Assistant ? MEAIChatRole.Assistant
                    : skMsg.Role == AuthorRole.User ? MEAIChatRole.User
                    : skMsg.Role == AuthorRole.Tool ? MEAIChatRole.Tool
                    : MEAIChatRole.System;

                result.Add(new MEAIChatMessage(role, contents));
            }
        }

        return result;
    }
}
