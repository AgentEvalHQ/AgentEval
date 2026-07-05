// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M4) — wires an off-hot-path <see cref="ShadowJudgePump"/> into the agent run seam. The
/// run returns to the caller UNCHANGED and immediately; a snapshot is then enqueued for asynchronous judgement.
/// An adverse verdict arms quarantine (see <see cref="QuarantineGate"/>) so a LATER run fails closed — it never
/// blocks the run it observed.
/// </summary>
public static class AgentEvalShadowJudgeExtensions
{
    /// <summary>Adds asynchronous shadow judgement over the run's response. The caller owns the <paramref name="pump"/>.</summary>
    public static AIAgentBuilder UseAgentEvalShadowJudge(this AIAgentBuilder builder, ShadowJudgePump pump)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pump);

        return builder.Use(
            runFunc: async (messages, session, options, innerAgent, ct) =>
            {
                var response = await innerAgent.RunAsync(messages, session, options, ct).ConfigureAwait(false);
                pump.Enqueue(new ShadowJudgeContext(response.Text, ConcatText(messages), innerAgent.Name, session));
                return response;
            },
            runStreamingFunc: (messages, session, options, innerAgent, ct) =>
                StreamAndEnqueue(pump, messages, session, options, innerAgent, ct));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> StreamAndEnqueue(
        ShadowJudgePump pump,
        IEnumerable<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var builder = new StringBuilder();
        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false).WithCancellation(ct))
        {
            builder.Append(update.Text);
            yield return update;
        }

        // Enqueue only after the full response streamed (an abandoned stream judges nothing).
        pump.Enqueue(new ShadowJudgeContext(builder.ToString(), ConcatText(messages), innerAgent.Name, session));
    }

    private static string ConcatText(IEnumerable<ChatMessage> messages)
        => string.Join("\n", messages.Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));
}
