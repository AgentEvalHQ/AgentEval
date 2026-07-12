// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// One <c>gatekeeper inspect</c> input payload — either a text-gate shape (<c>{ "text": "…" }</c>) or a tool-gate
/// shape (<c>{ "tool": "…", "args": { … }, "messages": [ … ] }</c>). Parsed from a single JSON object (stdin) or one
/// per line for <c>--input</c> batch.
/// </summary>
internal sealed class InspectPayload
{
    public string? Text { get; set; }
    public string? Tool { get; set; }
    public Dictionary<string, object?>? Args { get; set; }
    public List<GateHistoryMessageDto>? Messages { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parse one payload object. Returns null with <paramref name="error"/> set on invalid/empty JSON.</summary>
    public static InspectPayload? Parse(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty payload (expected a JSON object on stdin, or one per line with --input)";
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<InspectPayload>(json, Options);
            if (payload is null)
            {
                error = "payload deserialized to null";
                return null;
            }

            return payload;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON payload: {ex.Message}";
            return null;
        }
    }
}
