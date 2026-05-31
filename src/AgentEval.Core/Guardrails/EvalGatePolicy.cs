// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>How an <see cref="EvalGatingChatClient"/> enforces a gate's adverse verdict.</summary>
public enum EvalGatePolicy
{
    /// <summary>Record the verdict into the trace, let the call through (default; safe from CI to production).</summary>
    WarnOnly,

    /// <summary>Throw <see cref="EvalGateRefusalException"/> before/after the model call. Opt-in.</summary>
    ThrowOnFail,

    /// <summary>
    /// Mutate the messages (pre) or response (post) and proceed. Opt-in. With post-gates this is rejected
    /// for streaming at RUNTIME (eager throw in <see cref="EvalGatingChatClient.GetStreamingResponseAsync"/>)
    /// — bytes already in flight cannot be redacted.
    /// </summary>
    Redact,
}
