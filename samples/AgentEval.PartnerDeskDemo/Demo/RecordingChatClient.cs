// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>
/// Sits directly under the agent and records every tool call the model asks for, before any Gatekeeper middleware
/// sees it.
/// </summary>
/// <remarks>
/// This is what makes "the agent still tried" provable rather than rhetorical. A pre-execution block returns a
/// refusal to the model and leaves no trace in the tool ledger; the attempt only exists in the model's own output,
/// which is exactly what this client captures.
/// </remarks>
public sealed class RecordingChatClient : DelegatingChatClient
{
    private readonly ToolCallJournal _journal;

    /// <summary>Wraps <paramref name="inner"/>, recording proposals into <paramref name="journal"/>.</summary>
    public RecordingChatClient(IChatClient inner, ToolCallJournal journal)
        : base(inner) => _journal = journal;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Record(response.Messages);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            updates.Add(update);
            yield return update;
        }

        Record(updates.ToChatResponse().Messages);
    }

    private void Record(IEnumerable<ChatMessage> produced)
    {
        var calls = produced
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .ToArray();
        if (calls.Length == 0)
        {
            return;
        }

        var turn = _journal.BeginTurn();
        foreach (var call in calls)
        {
            _journal.RecordProposal(
                turn,
                call.Name,
                call.Arguments as IReadOnlyDictionary<string, object?>
                    ?? call.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value)
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        }
    }
}
