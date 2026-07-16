// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// Bridges <c>Microsoft.Agents.CopilotStudio.Client</c>'s conversational, streaming-activity API
/// (<c>StartConversationAsync</c> / <c>AskQuestionAsync</c> → <c>IAsyncEnumerable&lt;IActivity&gt;</c>, Bot
/// Framework activity schema) into an <see cref="IChatClient"/>, so it can be wrapped in a MAF <c>ChatClientAgent</c>
/// and handed to <see cref="CopilotStudioAgentFactory.FromAgent"/> exactly like every other agent this CLI drives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated shim, not a reuse of <c>AzureChatAgentFactory</c>'s plumbing:</b> Copilot Studio's wire model
/// is fundamentally different from a chat-completions endpoint. There is no single request/response call — the
/// SDK exposes one long-lived, stateful conversation (<c>StartConversationAsync</c> once, then repeated
/// <c>AskQuestionAsync</c> calls that the SDK auto-correlates to a server-side <c>conversationId</c>), and each
/// call streams zero-or-more Bot Framework <see cref="IActivity"/> frames rather than chat-completion deltas.
/// </para>
/// <para>
/// <b>Fidelity ceiling (text-only).</b> Only <c>Type == "message"</c> activities with non-empty <c>Text</c> are
/// surfaced — matches <see cref="CopilotStudioAgentFactory"/>'s documented <c>SutTier.TextOnly</c> /
/// <c>EvidenceFidelity.Verbal</c> ceiling. Adaptive cards, suggested actions, and other rich-content activities are
/// silently dropped rather than fabricated as text; a turn with no message-typed activity legitimately returns an
/// empty response rather than inventing content.
/// </para>
/// <para>
/// <b>Session semantics.</b> One instance == one Copilot Studio conversation. The very first call starts the
/// conversation (<c>emitStartConversationEvent: false</c> — this shim answers exactly the caller's first question;
/// it does not want a Topic-triggered greeting mixed into turn 1's transcript uninvited) and remembers the
/// server-issued <see cref="IActivity.Conversation"/>.<c>Id</c> for every subsequent
/// <c>AskQuestionAsync(question, conversationId)</c> call. This matches
/// <c>CopilotStudioRedTeamTarget.Validate</c>'s existing <c>--parallelism 1</c> gate (a live MCS session is
/// stateful/non-reentrant) — this class is not safe to call concurrently from multiple logical conversations, and
/// enforces that with an internal turn lock rather than relying solely on the caller's discipline.
/// </para>
/// <para>
/// <b>NOT independently live-verified.</b> The activity filtering/mapping logic below is unit-tested against a
/// fake <see cref="ICopilotStudioConversationClient"/> (see <c>CopilotStudioChatClientTests</c>), which proves the
/// bridge's shape is correct for the documented API contract. It has not been exercised against a real Copilot
/// Studio agent's actual wire traffic — see the CHANGELOG entry for what a pre-production smoke test must still
/// confirm (e.g. whether a real agent emits non-message activities on <c>StartConversationAsync</c> that should
/// also be surfaced, and the real shape of multi-activity turns).
/// </para>
/// </remarks>
internal sealed class CopilotStudioChatClient : IChatClient
{
    private readonly ICopilotStudioConversationClient _client;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private string? _conversationId;
    private bool _started;
    private bool _disposed;

    public CopilotStudioChatClient(ICopilotStudioConversationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var question = LastUserText(messages)
            ?? throw new InvalidOperationException(
                "CopilotStudioChatClient requires at least one user-role message with text to ask Copilot Studio.");

        // A caller-supplied ChatOptions.ConversationId (the MEAI "stateful client" convention — see
        // https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai#stateless-vs-stateful-clients) takes
        // precedence over this instance's own memoized id: it means the caller is deliberately resuming a specific
        // server-side conversation rather than continuing whatever this instance last tracked.
        if (!string.IsNullOrEmpty(options?.ConversationId))
        {
            _conversationId = options.ConversationId;
            _started = true;
        }

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                // P6 item B: retry the WHOLE call on a 429-shaped failure, not a partially-consumed stream —
                // materializing before yielding is the only way to retry a stream correctly (activities
                // already yielded to the caller can't be "un-yielded" on a mid-stream failure).
                var startActivities = await CopilotStudioRetryPolicy.Default.ExecuteAsync(
                    ct => DrainAsync(_client.StartConversationAsync(emitStartConversationEvent: false, cancellationToken: ct), ct),
                    cancellationToken).ConfigureAwait(false);

                foreach (var startActivity in startActivities)
                {
                    TrackConversationId(startActivity);
                    foreach (var update in AsUpdates(startActivity))
                    {
                        yield return update;
                    }
                }

                _started = true;
            }

            var askActivities = await CopilotStudioRetryPolicy.Default.ExecuteAsync(
                ct => DrainAsync(_client.AskQuestionAsync(question, _conversationId, ct), ct),
                cancellationToken).ConfigureAwait(false);

            foreach (var activity in askActivities)
            {
                TrackConversationId(activity);
                foreach (var update in AsUpdates(activity))
                {
                    yield return update;
                }
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>Materializes an activity stream into a list so <see cref="CopilotStudioRetryPolicy"/> can retry the WHOLE call as one unit.</summary>
    private static async Task<List<IActivity>> DrainAsync(IAsyncEnumerable<IActivity> source, CancellationToken ct)
    {
        var list = new List<IActivity>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            list.Add(item);
        }

        return list;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _turnLock.Dispose();
        if (_client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }
    }

    private IEnumerable<ChatResponseUpdate> AsUpdates(IActivity activity)
    {
        if (!string.Equals(activity.Type, ActivityTypes.Message, StringComparison.Ordinal))
        {
            yield break; // fidelity ceiling: non-message activities (typing, events, adaptive cards) are dropped, not fabricated as text.
        }

        if (string.IsNullOrEmpty(activity.Text))
        {
            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, activity.Text) { ConversationId = _conversationId };
    }

    private void TrackConversationId(IActivity activity)
    {
        var id = activity.Conversation?.Id;
        if (!string.IsNullOrEmpty(id))
        {
            _conversationId = id;
        }
    }

    private static string? LastUserText(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? last = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                last = message;
            }
        }

        return string.IsNullOrEmpty(last?.Text) ? null : last.Text;
    }
}
