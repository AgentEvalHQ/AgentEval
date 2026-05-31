// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEval.Tracing;

/// <summary>
/// Shared MEAI → trace-model mapping used by BOTH the chat-boundary recorder
/// (<see cref="TraceRecordingChatClient"/>) and the agent-boundary reconstruction
/// (<see cref="AgentBoundaryTraceBuilder"/>).
/// </summary>
/// <remarks>
/// Centralised deliberately: Trace Fidelity reconciliation compares tool-call arguments by
/// <b>serialized-string equality</b> (<c>TraceFidelityRunner.ArgumentDrift</c>). If the two layers
/// serialized the same <see cref="FunctionCallContent.Arguments"/> differently (key order, spacing,
/// number formatting), a clean run would show a <i>fabricated</i> argument_drift. Both layers calling
/// this one method makes byte-identical serialization structural rather than a copy-paste invariant.
/// </remarks>
internal static class TraceMapping
{
    /// <summary>Maps one MEAI tool call to the trace model, serializing arguments canonically.</summary>
    public static TraceToolCall ToToolCall(FunctionCallContent call) => new()
    {
        Name = call.Name,
        Arguments = call.Arguments is null ? null : JsonSerializer.Serialize(call.Arguments),
    };

    /// <summary>Maps MEAI usage (long counts) to the trace model (int), or null when absent.</summary>
    public static TraceTokenUsage? ToTokenUsage(UsageDetails? usage) => usage is null
        ? null
        : new TraceTokenUsage
        {
            PromptTokens = (int)(usage.InputTokenCount ?? 0),
            CompletionTokens = (int)(usage.OutputTokenCount ?? 0),
        };
}
