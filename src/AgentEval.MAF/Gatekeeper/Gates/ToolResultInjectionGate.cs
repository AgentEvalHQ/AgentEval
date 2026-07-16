// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Gates;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper Hardening Phase 2, P0-3 — a built-in <see cref="IToolResultGate"/> that blocks a tool result
/// carrying a prompt-injection marker (e.g. "ignore previous instructions"). Closes the "tool output as an
/// injection channel" gap for the OWASP LLM01 indirect-injection surface: a poisoned web page, file, or API
/// response a tool fetches can itself carry instructions aimed at the model rather than the user — this
/// inspects the RESULT text before it re-enters the model's context, at the same MAF seam <see cref="IToolGate"/>
/// inspects the proposed call, but on the other side of execution.
/// <para>Reuses the exact default marker set as the chat-side <see cref="TokenInjectionGate"/> (case-insensitive
/// substring match) — same detection logic, applied to a tool's output instead of a chat message, so the two
/// seams cannot silently drift out of sync with each other.</para>
/// <para><b>Not maskable, like its chat-side counterpart:</b> an injection marker's danger is the surrounding
/// instruction text, not a substring that can be safely blanked out while leaving the rest intact — so this
/// gate only ever returns <see cref="ToolResultAction.Block"/>, never <see cref="ToolResultAction.Redact"/>.
/// </para>
/// </summary>
public sealed class ToolResultInjectionGate : IToolResultGate
{
    private readonly string[] _tokens;

    /// <summary>Creates the gate with the default injection markers (shared with <see cref="TokenInjectionGate"/>), or a custom set when provided.</summary>
    public ToolResultInjectionGate(IEnumerable<string>? tokens = null)
        => _tokens = (tokens ?? TokenInjectionGate.DefaultTokens).ToArray();

    /// <inheritdoc/>
    public string PolicyName => "tool-result-prompt-injection";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = GateText.Stringify(result.Result);
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        var hits = _tokens.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        var verdict = hits.Count == 0
            ? ToolResultVerdict.Allow(PolicyName)
            : ToolResultVerdict.Block(PolicyName, $"tool '{result.FunctionName}' result contains injection marker(s): {string.Join(", ", hits)}");
        return new ValueTask<ToolResultVerdict>(verdict);
    }
}
