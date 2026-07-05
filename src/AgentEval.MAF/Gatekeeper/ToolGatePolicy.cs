// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How a tool-gate <see cref="ToolGateAction.Block"/> is enforced. Defaults to <see cref="WarnOnly"/> (matches
/// the shipped chat-gate default) so adding a gate never silently changes an agent's behavior; callers opt into
/// blocking for production. A <see cref="ToolGateAction.Mutate"/> verdict is applied regardless of policy.
/// </summary>
public enum ToolGatePolicy
{
    /// <summary>Record <c>gate.tool.*</c> evidence but let the tool run (safe default).</summary>
    WarnOnly,

    /// <summary>Block: replace the tool result with a refusal string the model sees; never execute the tool.</summary>
    ReplaceResult,

    /// <summary>Block AND stop the function-calling loop (set <c>FunctionInvocationContext.Terminate</c>).</summary>
    Terminate,

    // NOTE: there is intentionally no "ThrowOnFail" here. A throw at the function-invocation seam is CAUGHT by
    // MAF's FunctionInvokingChatClient (it converts tool exceptions into an error result and continues), so it
    // never reaches the caller. Hard-failing a run belongs at the run/chat gate (M2, EvalGatePolicy.ThrowOnFail),
    // where the exception propagates. Verified empirically 2026-07-05.
}
