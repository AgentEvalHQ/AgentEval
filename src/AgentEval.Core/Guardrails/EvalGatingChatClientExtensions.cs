// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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
    /// <param name="builder">The chat client builder.</param>
    /// <param name="pre">Run-pre gates, inspecting the input text before the model sees it.</param>
    /// <param name="post">Run-post gates, inspecting the response text.</param>
    /// <param name="policy">How a gate's <see cref="GateAction.Block"/> finding is enforced.</param>
    /// <param name="trace">Optional Glass Box trace every gate verdict is recorded into.</param>
    /// <param name="correlator">
    /// Optional, session-scoped Fleet Correlation Layer (Experimental) — see
    /// <see cref="EvalGatingChatClient"/>'s matching constructor parameter and <see cref="FleetCorrelator"/>'s
    /// own remarks.
    /// </param>
#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001 // the documented, intended consumption point — see EvalGatingChatClient's own file-wide suppression note.
    public static ChatClientBuilder UseEvalGate(
        this ChatClientBuilder builder,
        IReadOnlyList<IChatGate>? pre = null,
        IReadOnlyList<IChatGate>? post = null,
        EvalGatePolicy policy = EvalGatePolicy.WarnOnly,
        AgentTrace? trace = null,
        FleetCorrelator? correlator = null)
        => builder.Use(inner => new EvalGatingChatClient(inner, pre, post, policy, trace, correlator));
#pragma warning restore AGENTEVAL_GATEKEEPER_PREVIEW001
}
