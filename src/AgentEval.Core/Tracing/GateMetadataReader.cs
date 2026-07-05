// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Tracing;

/// <summary>
/// Reads the runtime-gate verdicts that <c>EvalGatingChatClient</c> records into
/// <see cref="AgentTrace.Metadata"/> under keys shaped <c>gate.{stage}.{seq}.{policy}</c>.
/// </summary>
/// <remarks>
/// In memory the recorded value is a <see cref="IReadOnlyDictionary{TKey,TValue}"/>; after a trace is
/// serialized and reloaded it deserializes to a <see cref="JsonElement"/>. This reader handles BOTH shapes
/// so gate-block evidence (compliance, auto-audit) is not silently lost — counting zero blocks — on the
/// persisted path. Centralised here so every consumer reads gate verdicts identically.
/// </remarks>
public static class GateMetadataReader
{
    /// <summary>True when <paramref name="key"/> is a gate-verdict metadata key.</summary>
    public static bool IsGateKey(string key) => key.StartsWith("gate.", StringComparison.Ordinal);

    /// <summary>True when the recorded verdict value carries <c>action == "Block"</c> (either shape).</summary>
    public static bool IsBlock(object? value)
        => string.Equals(ReadAction(value), "Block", StringComparison.Ordinal);

    /// <summary>Extracts the policy name from a <c>gate.{stage}.{seq}.{policy}</c> key, or null if malformed.</summary>
    public static string? PolicyFromKey(string key)
        => TrySplitGateKey(key, out var parts) ? string.Join('.', parts[3..]) : null;

    /// <summary>
    /// Extracts the stage token from a <c>gate.{stage}.{seq}.{policy}</c> key, or null if malformed. Stage
    /// tokens are dot-free (<c>pre</c>, <c>post</c>, <c>tool</c>, <c>run-pre</c>, <c>run-post</c>).
    /// </summary>
    public static string? StageFromKey(string key)
        => TrySplitGateKey(key, out var parts) ? parts[1] : null;

    // A well-formed gate-verdict key is exactly gate.{stage}.{seq}.{policy} — the "gate." prefix plus non-empty
    // stage, seq, and policy. The policy may itself contain dots (e.g. "My.Policy"), but EVERY policy segment must
    // be non-empty, so keys with a leading/trailing/double dot in the policy (e.g. "gate.pre.1..Policy") are
    // rejected. This keeps the extractors returning null on a malformed key rather than misclassifying it.
    private static bool TrySplitGateKey(string key, out string[] parts)
    {
        parts = key.Split('.');
        return parts.Length >= 4
            && parts[0] == "gate"
            && parts[1].Length > 0                          // stage
            && parts[2].Length > 0                          // seq
            && parts[3..].All(static p => p.Length > 0);    // policy — no empty segments (no leading/trailing/double dot)
    }

    private static string? ReadAction(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> dict => dict.TryGetValue("action", out var a) ? a as string : null,
        JsonElement je when je.ValueKind == JsonValueKind.Object =>
            je.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
        _ => null,
    };
}
