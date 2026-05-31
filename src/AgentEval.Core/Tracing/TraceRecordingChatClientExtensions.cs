// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Tracing;

/// <summary>Builder extension that inserts a <see cref="TraceRecordingChatClient"/> into a chat pipeline.</summary>
public static class TraceRecordingChatClientExtensions
{
    /// <summary>
    /// Records every LLM round-trip into <paramref name="trace"/>. Compose this <b>inner</b> of
    /// <c>UseFunctionInvocation</c> to capture each round-trip of the tool loop:
    /// <c>client.AsBuilder().UseFunctionInvocation().UseTraceRecording("planner", trace).Build()</c>.
    /// </summary>
    public static ChatClientBuilder UseTraceRecording(
        this ChatClientBuilder builder, string agentName, AgentTrace trace, SamplePreset preset = SamplePreset.Standard)
        => builder.Use(inner => new TraceRecordingChatClient(inner, agentName, trace, preset));
}
