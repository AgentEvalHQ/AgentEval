// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>A function call in a history message: <c>{ callId, name }</c>.</summary>
internal sealed record FunctionCallDto(string? CallId = null, string? Name = null);

/// <summary>A tool result in a history message: <c>{ callId, result }</c> (result is any JSON value).</summary>
internal sealed record FunctionResultDto(string? CallId = null, object? Result = null);

/// <summary>
/// One message of the run history a caller passes to a history-reading tool gate (e.g. referential-integrity,
/// taint-tracking). Exactly one of <see cref="Text"/> / <see cref="FunctionCall"/> / <see cref="FunctionResult"/>
/// is expected per message.
/// </summary>
internal sealed record GateHistoryMessageDto(
    string? Role = null,
    string? Text = null,
    FunctionCallDto? FunctionCall = null,
    FunctionResultDto? FunctionResult = null);

/// <summary>
/// Maps the JSON history DTO to MEAI <see cref="ChatMessage"/>s the tool gates recompute from. MEAI content is not
/// trivially JSON-round-trippable, so this mapping is hand-written and <b>fail-closed</b>: an unknown role, an
/// entirely-empty message, or one carrying more than one payload shape returns <c>null</c> (mapping failed) so the caller emits <c>GateInconclusive</c> (exit 6)
/// rather than handing a gate an empty history it would read as "nothing to flag → Allow".
/// </summary>
internal static class GateHistoryMapper
{
    /// <summary>Maps the DTOs to chat messages, or returns null (with <paramref name="error"/> set) on any bad message.</summary>
    public static IReadOnlyList<ChatMessage>? ToChatMessages(IEnumerable<GateHistoryMessageDto>? dtos, out string? error)
    {
        error = null;
        if (dtos is null)
        {
            return Array.Empty<ChatMessage>();
        }

        var result = new List<ChatMessage>();
        foreach (var m in dtos)
        {
            if (!TryRole(m.Role, out var role))
            {
                error = $"unknown message role '{m.Role}' (expected user|assistant|tool|system)";
                return null;
            }

            // Exactly one payload shape per message. Silently preferring one when several are set would drop the
            // others and launder a caller mistake (e.g. text alongside a functionResult) — fail closed instead.
            var shapes = (string.IsNullOrEmpty(m.Text) ? 0 : 1)
                       + (m.FunctionCall is null ? 0 : 1)
                       + (m.FunctionResult is null ? 0 : 1);
            if (shapes > 1)
            {
                error = "history message must carry exactly one of text, functionCall, or functionResult";
                return null;
            }

            if (m.FunctionCall is { } fc)
            {
                // A missing callId/name is structurally invalid — fail closed rather than coerce to empty strings
                // (an empty callId could falsely "pair" with an empty-callId result and launder a taint/id check).
                if (string.IsNullOrEmpty(fc.CallId) || string.IsNullOrEmpty(fc.Name))
                {
                    error = "functionCall requires a non-empty callId and name";
                    return null;
                }

                result.Add(new ChatMessage(role, [new FunctionCallContent(fc.CallId, fc.Name, arguments: null)]));
            }
            else if (m.FunctionResult is { } fr)
            {
                if (string.IsNullOrEmpty(fr.CallId))
                {
                    error = "functionResult requires a non-empty callId";
                    return null;
                }

                result.Add(new ChatMessage(role, [new FunctionResultContent(fr.CallId, fr.Result)]));
            }
            else if (!string.IsNullOrEmpty(m.Text))
            {
                result.Add(new ChatMessage(role, m.Text));
            }
            else
            {
                error = "history message has no text, functionCall, or functionResult";
                return null;
            }
        }

        return result;
    }

    private static bool TryRole(string? role, out ChatRole chatRole)
    {
        switch (role?.Trim().ToLowerInvariant())
        {
            case "user": chatRole = ChatRole.User; return true;
            case "assistant": chatRole = ChatRole.Assistant; return true;
            case "tool": chatRole = ChatRole.Tool; return true;
            case "system": chatRole = ChatRole.System; return true;
            default: chatRole = ChatRole.User; return false;
        }
    }
}
