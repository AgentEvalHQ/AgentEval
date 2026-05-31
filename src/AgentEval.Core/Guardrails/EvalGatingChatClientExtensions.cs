// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails;

/// <summary>Builder extension that inserts an <see cref="EvalGatingChatClient"/> into a chat pipeline.</summary>
public static class EvalGatingChatClientExtensions
{
    /// <summary>
    /// Adds pre/post policy gates. Place <c>UseEvalGate(pre: …)</c> <b>outermost</b> (it must see the
    /// original user input). The Redact-streaming rejection is enforced at <b>runtime</b> (in
    /// <see cref="EvalGatingChatClient.GetStreamingResponseAsync"/>), not at build time — the builder cannot
    /// know whether the caller will stream.
    /// </summary>
    public static ChatClientBuilder UseEvalGate(
        this ChatClientBuilder builder,
        IReadOnlyList<IChatGate>? pre = null,
        IReadOnlyList<IChatGate>? post = null,
        EvalGatePolicy policy = EvalGatePolicy.WarnOnly,
        AgentTrace? trace = null)
        => builder.Use(inner => new EvalGatingChatClient(inner, pre, post, policy, trace));
}
