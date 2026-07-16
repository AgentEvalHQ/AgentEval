// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.Core.Models;
using CopilotClient = Microsoft.Agents.CopilotStudio.Client.CopilotClient;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// AgentEval-owned abstraction over the two <c>CopilotClient</c> members <see cref="CopilotStudioChatClient"/>
/// needs. <c>Microsoft.Agents.CopilotStudio.Client</c>'s public surface (verified by reflecting on the real,
/// restored 1.3.171-beta assembly — its actual exported types are narrower than early decompilation notes
/// suggested) does <b>not</b> publicly export an <c>ICopilotClient</c> interface for <c>CopilotClient</c> to
/// implement, so this repo defines its own seam instead of depending on one that doesn't exist. This is also what
/// makes <see cref="CopilotStudioChatClient"/> unit-testable without a live Copilot Studio agent — see
/// <c>FakeCopilotStudioConversationClient</c> in <c>CopilotStudioChatClientTests</c>.
/// </summary>
internal interface ICopilotStudioConversationClient
{
    /// <summary>Starts a new Copilot Studio conversation. See <c>CopilotClient.StartConversationAsync</c>.</summary>
    IAsyncEnumerable<IActivity> StartConversationAsync(bool emitStartConversationEvent, CancellationToken cancellationToken);

    /// <summary>Asks a question within a conversation. See <c>CopilotClient.AskQuestionAsync(string, string?, CancellationToken)</c>.</summary>
    IAsyncEnumerable<IActivity> AskQuestionAsync(string question, string? conversationId, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="ICopilotStudioConversationClient"/> — a direct, no-op-else pass-through to a real <see cref="CopilotClient"/>.</summary>
internal sealed class CopilotClientConversationAdapter : ICopilotStudioConversationClient
{
    private readonly CopilotClient _client;

    public CopilotClientConversationAdapter(CopilotClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IAsyncEnumerable<IActivity> StartConversationAsync(bool emitStartConversationEvent, CancellationToken cancellationToken)
        => _client.StartConversationAsync(emitStartConversationEvent, cancellationToken);

    public IAsyncEnumerable<IActivity> AskQuestionAsync(string question, string? conversationId, CancellationToken cancellationToken)
        => _client.AskQuestionAsync(question, conversationId, cancellationToken);
}
