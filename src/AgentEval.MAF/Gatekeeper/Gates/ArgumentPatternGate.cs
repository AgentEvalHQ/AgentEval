// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Assertions;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that blocks a call whose serialized arguments match a forbidden pattern (e.g. a
/// path traversal, a secret shape, an injected command). Uses the shared ReDoS-safe, fail-closed
/// <see cref="ForbiddenPatternScanner"/> (a match timeout is treated as a Block — "could not prove clean").
/// </summary>
public sealed class ArgumentPatternGate : IToolGate
{
    // CRITICAL: use the relaxed encoder. Default JSON escaping turns injection metacharacters (< > & ' +) into
    // \uXXXX / entity forms, so a pattern like "<script" or "' OR" would NEVER match the escaped surface and the
    // gate would fail OPEN. The relaxed encoder leaves those bytes as the tool will actually receive them.
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Regex _forbidden;
    private readonly bool _canonicalize;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>Creates the gate from a pattern, compiled with a bounded (100 ms) match timeout (ReDoS-safe).</summary>
    public ArgumentPatternGate(string forbiddenPattern, string? policyName = null, bool canonicalize = false)
        : this(new Regex(forbiddenPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), policyName, canonicalize)
    {
    }

    /// <summary>Creates the gate from a caller-supplied <see cref="Regex"/> (must have a bounded MatchTimeout). Set
    /// <paramref name="canonicalize"/> to also scan decoded projections (percent / HTML-entity / unicode-escape /
    /// base64) of the arguments via <see cref="ArgumentCanonicalizer"/>, so a payload the tool later decodes cannot
    /// evade the pattern by arriving encoded (Fable 5 §13).</summary>
    public ArgumentPatternGate(Regex forbiddenPattern, string? policyName = null, bool canonicalize = false)
    {
        ArgumentNullException.ThrowIfNull(forbiddenPattern);
        if (forbiddenPattern.MatchTimeout == Regex.InfiniteMatchTimeout)
        {
            throw new ArgumentException(
                "forbiddenPattern must have a bounded MatchTimeout (ReDoS guard).", nameof(forbiddenPattern));
        }

        _forbidden = forbiddenPattern;
        _canonicalize = canonicalize;
        PolicyName = policyName ?? "ArgumentPatternGate";
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(call.Arguments, ScanOptions);
        }
        catch (NotSupportedException)
        {
            // Cannot inspect the arguments ⇒ fail closed (cannot prove clean).
            return new ValueTask<ToolGateVerdict>(
                ToolGateVerdict.Block(PolicyName, $"tool '{call.FunctionName}' arguments could not be serialized for inspection"));
        }

        // Default: scan the raw serialized surface. With canonicalize: also scan each decoded projection so an
        // encoded payload the tool later decodes cannot slip past (the raw surface is the first projection).
        var hit = _canonicalize
            ? ArgumentCanonicalizer.Canonicalize(serialized).Any(p => ForbiddenPatternScanner.ScanForForbiddenPattern(p, _forbidden))
            : ForbiddenPatternScanner.ScanForForbiddenPattern(serialized, _forbidden);

        return new ValueTask<ToolGateVerdict>(hit
            ? ToolGateVerdict.Block(PolicyName, $"tool '{call.FunctionName}' arguments matched a forbidden pattern")
            : ToolGateVerdict.Allow(PolicyName));
    }
}
