// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, #13) — renders a tool call's arguments into <c>gate.tool.*</c> mutation-evidence text
/// per <see cref="TraceCaptureMode"/>. Used by <c>AgentEvalToolGateExtensions</c>'s <c>Mutate</c> handling for
/// both the "before" and "after" argument snapshots.
/// </summary>
internal static class MutationEvidenceRenderer
{
    /// <summary>Renders <paramref name="args"/> per <paramref name="mode"/>.</summary>
    public static string Render(IReadOnlyDictionary<string, object?>? args, TraceCaptureMode mode)
    {
        if (mode == TraceCaptureMode.None)
        {
            return "(not captured — TraceCaptureMode.None)";
        }

        if (args is null || args.Count == 0)
        {
            return "{}";
        }

        var projected = new Dictionary<string, object?>(args.Count, StringComparer.Ordinal);
        foreach (var kv in args)
        {
            projected[kv.Key] = mode switch
            {
                TraceCaptureMode.SchemaOnly => kv.Value is null ? "null" : kv.Value.GetType().Name,
                TraceCaptureMode.Redacted => kv.Value is null ? null : "***",
                TraceCaptureMode.Hashed => kv.Value is null ? null : Hash(kv.Value),
                TraceCaptureMode.Full => kv.Value,
                _ => kv.Value is null ? null : "***",   // defensive fallback: never accidentally leak
            };
        }

        return Serialize(projected);
    }

    // Reuses the shared SHA-256-hex primitive (AgentEval.Guardrails.ManifestFingerprint, already used for
    // skill/tool-manifest drift hashing) instead of a second hand-rolled SHA256+hex implementation. Truncated
    // to 16 hex chars — this is a "did the value change/repeat" telemetry marker, not a security-critical
    // digest, so the shorter form is deliberate; truncating a lowercase hex string is position-invariant, so
    // this is byte-for-byte the same output the previous local implementation produced.
    private static string Hash(object value)
        => "sha256:" + ManifestFingerprint.Hash(GateText.Stringify(value))[..16];

    private static string Serialize(Dictionary<string, object?> projected)
    {
        try
        {
            // Relaxed encoder (shared with GateText) so a FULL-mode capture is FAITHFUL (not JSON-escaped):
            // default escaping would render < > & ' and non-ASCII as \uXXXX, so the mutation audit would not
            // match the values the tool actually receives. SchemaOnly/Redacted/Hashed never emit user-controlled
            // bytes, so this only matters for Full.
            return JsonSerializer.Serialize(projected, GateText.SerializerOptions);
        }
        catch (NotSupportedException)
        {
            return "(unserializable)";
        }
    }
}
