// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, #13) — renders a tool call's arguments into <c>gate.tool.*</c> mutation-evidence text
/// per <see cref="TraceCaptureMode"/>. Used by <c>AgentEvalToolGateExtensions</c>'s <c>Mutate</c> handling for
/// both the "before" and "after" argument snapshots.
/// </summary>
internal static class MutationEvidenceRenderer
{
    // Relaxed encoder so a FULL-mode capture is FAITHFUL (not JSON-escaped): default escaping would render
    // < > & ' and non-ASCII as \uXXXX, so the mutation audit would not match the values the tool actually
    // receives. SchemaOnly/Redacted/Hashed never emit user-controlled bytes, so this only matters for Full.
    private static readonly JsonSerializerOptions RelaxedOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    private static string Hash(object value)
    {
        var bytes = Encoding.UTF8.GetBytes(GateText.Stringify(value));
        var digest = SHA256.HashData(bytes);
        // Convert.ToHexStringLower is net9.0+; this project's floor is net8.0, so lower-case explicitly.
        return "sha256:" + Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    private static string Serialize(Dictionary<string, object?> projected)
    {
        try
        {
            return JsonSerializer.Serialize(projected, RelaxedOptions);
        }
        catch (NotSupportedException)
        {
            return "(unserializable)";
        }
    }
}
