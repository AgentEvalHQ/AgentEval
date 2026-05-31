// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// A single tool/function invocation captured as part of an eval input.
/// <para>
/// Within <see cref="EvalInput.ToolCalls"/> these are ordered <b>chronologically</b> by
/// invocation (earliest first); the record itself carries no timestamp, so the list position
/// <i>is</i> the time order. See the ordering contract on <see cref="EvalInput.ToolCalls"/>.
/// </para>
/// </summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object>? Arguments, string? Result);

/// <summary>Definition of a tool that was available to the agent during the evaluated interaction.</summary>
public sealed record ToolDefinition(string Name, string? Description, IReadOnlyDictionary<string, object>? Parameters);

/// <summary>An expected agentic action used to verify tool-use behaviour.</summary>
public sealed record ExpectedAction(string Description, IReadOnlyList<string>? RequiredTools);

/// <summary>Intentionally permissive input container shared across all eval types.</summary>
/// <remarks>
/// <b>ToolCalls ordering contract:</b> <see cref="ToolCalls"/> is ordered <b>chronologically</b>
/// by invocation (earliest first). This is a contract, not a convenience: order-sensitive
/// evaluators rely on the list position as the call's time order — the tool-sequence assertions
/// (<c>WithArgument</c>/<c>BeforeTool</c>/<c>AfterTool</c>, <c>MustConfirmBefore</c>) and the
/// prohibited-actions approval gate (which treats an approval as valid only if it appears at an
/// earlier index than the sensitive call). Callers MUST populate <see cref="ToolCalls"/>
/// chronologically; supplying it out of order yields undefined safety-gate results (BUG-37).
/// </remarks>
public sealed record EvalInput(
    string Query,
    string? Response = null,
    string? Context = null,
    string? GroundTruth = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    IReadOnlyList<ToolDefinition>? ToolDefinitions = null,
    IReadOnlyList<ExpectedAction>? ExpectedActions = null,
    string? SystemMessage = null,
    IReadOnlyDictionary<string, object>? Metadata = null);
