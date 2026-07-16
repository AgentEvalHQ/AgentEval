// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// An <see cref="IChatGate"/> that blocks when the text contains any configured injection marker/phrase
/// (case-insensitive substring match). Mirrors the Red Team <c>ContainsTokenEvaluator</c> intent for a
/// pre-flight input gate. Injection findings are not maskable, so it never supplies redacted text.
/// </summary>
public sealed class TokenInjectionGate : IChatGate
{
    /// <summary>
    /// The default injection markers this gate scans for. Public (not just an internal default) so a sibling
    /// detector at a different seam — e.g. <c>AgentEval.MAF.Gatekeeper.ToolResultInjectionGate</c>,
    /// scanning a tool's RESULT instead of the run's input — can reuse the exact same marker list rather than
    /// maintaining a second copy that could silently drift out of sync.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTokens = new[]
    {
        "ignore previous instructions",
        "ignore all previous instructions",
        "disregard the above",
        "you are now",
        "system prompt:",
        "reveal your instructions",
    };

    private readonly string[] _tokens;

    /// <summary>Creates a gate with the default injection markers, or a custom set when provided.</summary>
    public TokenInjectionGate(IEnumerable<string>? tokens = null)
        => _tokens = (tokens ?? DefaultTokens).ToArray();

    /// <inheritdoc />
    public string PolicyName => "prompt-injection";

    /// <inheritdoc />
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        var hits = _tokens.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        var verdict = hits.Count == 0
            ? GateVerdict.Allow(PolicyName)
            : GateVerdict.Block(PolicyName, $"Injection marker(s) detected: {string.Join(", ", hits)}", hits);
        return new ValueTask<GateVerdict>(verdict);
    }
}
