// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// A pre- or post-flight check on chat content for the runtime policy gate (<see cref="EvalGatingChatClient"/>).
/// Pure, fast, and deterministic where possible. The same policy intent as the post-hoc behavioural
/// assertions (NeverCallTool, etc.), applied inline on live traffic.
/// </summary>
public interface IChatGate
{
    /// <summary>Stable policy name recorded into the trace and exceptions.</summary>
    string PolicyName { get; }

    /// <summary>
    /// Inspect text — the concatenated user prompt pre-flight, or the model reply post-flight — and
    /// return a verdict (Allow / Block / Redact).
    /// </summary>
    ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default);
}
