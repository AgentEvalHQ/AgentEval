// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Guardrails;

/// <summary>
/// Construction-time provenance manifest for MCP tool definitions. It requires caller-supplied server identity
/// and pins each tool by the collision-safe pair <c>(ServerId, Name)</c>.
/// </summary>
/// <remarks>
/// This is not an <c>IToolGate</c> and does not claim runtime MCP interception. The caller that owns MCP
/// registration must copy the stable logical identity from its explicit connection descriptor into
/// <see cref="McpToolDefinition.ServerId"/>. Provider-hosted MCP remains opaque, and provenance is never inferred
/// from model-visible tool names, descriptions, or schemas.
/// </remarks>
public static class McpServerProvenanceGate
{
    /// <summary>
    /// Returns the collision-safe manifest key for <paramref name="tool"/> as a canonical JSON string pair
    /// <c>["server-id","tool-name"]</c>.
    /// </summary>
    public static string ManifestKey(McpToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ValidateIdentity(tool, nameof(tool));
        return JsonSerializer.Serialize(new[] { tool.ServerId!, tool.Name });
    }

    /// <summary>
    /// Captures a trust-time baseline keyed by <c>(ServerId, Name)</c>. Every tool must have explicit server
    /// identity; duplicate identities fail construction.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CaptureBaseline(IReadOnlyList<McpToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return CreateManifest(tools);
    }

    /// <summary>
    /// Checks current server-qualified tool definitions against a pinned provenance manifest.
    /// </summary>
    public static IReadOnlyList<ManifestDriftFinding> CheckDrift(
        IReadOnlyList<McpToolDefinition> currentTools,
        IReadOnlyDictionary<string, string> baselineHashesByServerAndTool)
    {
        ArgumentNullException.ThrowIfNull(currentTools);
        ArgumentNullException.ThrowIfNull(baselineHashesByServerAndTool);

        return ManifestDriftDetector.Detect(baselineHashesByServerAndTool, CreateManifest(currentTools));
    }

    private static IReadOnlyDictionary<string, string> CreateManifest(IReadOnlyList<McpToolDefinition> tools)
    {
        var manifest = new Dictionary<string, string>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ValidateIdentity(tool, nameof(tools));

            var key = JsonSerializer.Serialize(new[] { tool.ServerId!, tool.Name });
            if (!manifest.TryAdd(key, McpToolDescriptionPoisoningGate.Fingerprint(tool)))
            {
                throw new ArgumentException(
                    $"Expected unique MCP (ServerId, Name) identities. Actual: duplicate {key}. " +
                    "Suggestions: remove the duplicate registration or assign the correct explicit server identity. " +
                    "Because: two definitions with the same provenance identity cannot be pinned unambiguously.",
                    nameof(tools));
            }
        }

        return manifest;
    }

    private static void ValidateIdentity(McpToolDefinition tool, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(tool.ServerId))
        {
            throw new ArgumentException(
                $"Expected every MCP tool definition to have a non-empty ServerId. Actual: tool '{tool.Name}' " +
                "has no authoritative server identity. Suggestions: populate ServerId from the explicit MCP " +
                "connection registration descriptor. Because: provenance must never be inferred from a tool-name prefix.",
                parameterName);
        }

        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException(
                $"Expected every MCP tool definition to have a non-empty Name. Actual: server '{tool.ServerId}' " +
                "contains an unnamed tool. Suggestions: reject the malformed discovery result before manifest capture. " +
                "Because: an unnamed tool cannot have a stable provenance identity.",
                parameterName);
        }
    }
}
