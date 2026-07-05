// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The subject a tool gate inspects: one function call in flight at the MAF <c>FunctionInvocationContext</c>
/// boundary. Named <c>GatedToolCall</c> (not <c>ToolCallInfo</c>) to avoid clashing with the Core
/// <c>ToolCallRecord</c> / <c>ToolUsage</c> family.
/// </summary>
/// <param name="FunctionName">The tool/function name the model requested.</param>
/// <param name="Arguments">The arguments the model supplied (may be null).</param>
/// <param name="AgentName">The invoking agent's name, if known.</param>
/// <param name="Iteration">The FICC iteration index for this run.</param>
/// <param name="FunctionCallIndex">This call's index within the current iteration.</param>
/// <param name="FunctionCount">Total function calls in the current iteration.</param>
/// <param name="IsStreaming">Whether the run is streaming.</param>
/// <param name="Messages">The input chat history for this call (read-only view).</param>
public sealed record GatedToolCall(
    string FunctionName,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? AgentName,
    int Iteration,
    int FunctionCallIndex,
    int FunctionCount,
    bool IsStreaming,
    IReadOnlyList<ChatMessage>? Messages);
