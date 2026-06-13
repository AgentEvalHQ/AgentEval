// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam;

/// <summary>
/// An <b>active</b> <see cref="DelegatingChatClient"/> that splices an attacker payload into the model's INPUT for a
/// chosen <see cref="InjectionSurface"/> before delegating (Wave B, Pillar 4) — AgentDojo's "adversarial content at
/// tool-output placeholders". This MUTATES the request; the Glass-Box <c>TraceRecordingChatClient</c> only observes,
/// so compose the recorder alongside this to capture the resulting trace.
/// </summary>
public sealed class InjectingChatClient : DelegatingChatClient
{
    private readonly string _payload;
    private readonly InjectionSurface _surface;

    /// <summary>Wraps <paramref name="inner"/>, splicing <paramref name="payload"/> into the given <paramref name="surface"/>.</summary>
    public InjectingChatClient(IChatClient inner, string payload, InjectionSurface surface) : base(inner)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _surface = surface;
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Splice(messages), options, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Splice(messages), options, cancellationToken);

    private List<ChatMessage> Splice(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        if (_surface == InjectionSurface.UserMessage || list.Count == 0)
            return list;   // UserMessage is already inlined by the probe — nothing to splice at this boundary

        if (_surface == InjectionSurface.ToolOutput)
        {
            // Append the payload into the most recent tool result (FunctionResultContent), poisoning what the
            // model reads next without touching the user's own message.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var contents = list[i].Contents;
                for (var j = contents.Count - 1; j >= 0; j--)
                {
                    if (contents[j] is FunctionResultContent frc)
                    {
                        var newContents = new List<AIContent>(contents)
                        {
                            [j] = new FunctionResultContent(frc.CallId, $"{frc.Result}\n{_payload}")
                        };
                        list[i] = CloneWith(list[i], newContents);
                        return list;
                    }
                }
            }
            return list;   // no tool result present to splice into
        }

        // RetrievedDocument: append a retrieved-context block as a NEW content item on the latest user message —
        // preserving any existing (possibly non-text) content rather than collapsing the message to a single string.
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
            {
                var newContents = new List<AIContent>(list[i].Contents) { new TextContent($"\n\n[Retrieved context]\n{_payload}") };
                list[i] = CloneWith(list[i], newContents);
                return list;
            }
        }
        return list;
    }

    // Rebuild a message replacing only its Contents, carrying over the identity/metadata fields a bare
    // `new ChatMessage(role, contents)` would silently drop (review).
    private static ChatMessage CloneWith(ChatMessage original, IList<AIContent> contents) => new(original.Role, contents)
    {
        AuthorName = original.AuthorName,
        MessageId = original.MessageId,
        AdditionalProperties = original.AdditionalProperties,
        RawRepresentation = original.RawRepresentation,
    };
}
