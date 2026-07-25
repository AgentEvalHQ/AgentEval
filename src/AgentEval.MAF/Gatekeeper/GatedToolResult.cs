// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The subject a tool RESULT gate inspects: one already-executed function call's raw return value, at the
/// same MAF <c>FunctionInvocationContext</c> boundary <see cref="GatedToolCall"/> inspects the PROPOSED call.
/// Phase 1 Gatekeeper Hardening, P0-3 — closes the "tool output as an injection channel" gap: nothing before
/// this inspected a tool's own result before it re-entered the model's context (only <em>prior</em> results,
/// read out of conversation history by gates like <c>TaintTrackingGate</c>/<c>ReferentialIntegrityGate</c>,
/// were ever consulted — never <em>this</em> call's own result, synchronously, before it flows back).
/// </summary>
/// <param name="FunctionName">The tool/function name that was called.</param>
/// <param name="Arguments">The arguments the call actually ran with (post any <see cref="ToolGateAction.Mutate"/> rewrite).</param>
/// <param name="Result">The raw result the tool function produced — the exact object <c>next(...)</c> returned, before any result gate has touched it.</param>
/// <param name="AgentName">The invoking agent's name, if known.</param>
/// <param name="Iteration">The FICC iteration index for this run.</param>
/// <param name="FunctionCallIndex">This call's index within the current iteration.</param>
/// <param name="FunctionCount">Total function calls in the current iteration.</param>
/// <param name="IsStreaming">Whether the run is streaming.</param>
/// <param name="Messages">The input chat history for this call (read-only view).</param>
public sealed record GatedToolResult(
    string FunctionName,
    IReadOnlyDictionary<string, object?>? Arguments,
    object? Result,
    string? AgentName,
    int Iteration,
    int FunctionCallIndex,
    int FunctionCount,
    bool IsStreaming,
    IReadOnlyList<ChatMessage>? Messages)
{
    private string? _resultText;

    /// <summary>
    /// The <see cref="Result"/> serialized to text ONCE and cached (Phase 5, P5-5). Result gates read THIS instead
    /// of each calling <c>GateText.Stringify(Result)</c> — the tool-gate loop reuses one <see cref="GatedToolResult"/>
    /// across gates while the result is unchanged, so a large result is serialized once, not once per gate. (The
    /// cache field is excluded from record equality, which is over the ctor members only.)
    /// </summary>
    public string ResultText => _resultText ??= GateText.Stringify(Result);
}
