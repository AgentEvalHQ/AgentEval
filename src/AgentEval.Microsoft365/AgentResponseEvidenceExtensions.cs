// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// Reads the Microsoft 365 evidence document back from an <see cref="AgentResponse"/> produced by
/// <see cref="WorkIqEvaluableAgent"/>, where it travels under
/// <c>AdditionalProperties[<see cref="Microsoft365Evidence.Key"/>]</c>.
/// </summary>
public static class AgentResponseEvidenceExtensions
{
    /// <summary>
    /// The evidence document of the response, or null when the response did not come from this provider (no key,
    /// or a value that is not an evidence document). A <see cref="JsonElement"/> or JSON string value (a replayed
    /// response) is deserialized with <see cref="Microsoft365EvidenceJson"/>.
    /// </summary>
    /// <param name="response">The response.</param>
    public static Microsoft365TurnEvidence? Evidence(this AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return TryGetEvidence(response, out var evidence) ? evidence : null;
    }

    /// <summary>Tries to read the evidence document; see <see cref="Evidence"/> for what counts as one.</summary>
    /// <param name="response">The response.</param>
    /// <param name="evidence">The document when found.</param>
    public static bool TryGetEvidence(this AgentResponse response, out Microsoft365TurnEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(response);
        evidence = null;
        if (response.AdditionalProperties is null
            || !response.AdditionalProperties.TryGetValue(Microsoft365Evidence.Key, out var value)
            || value is null)
        {
            return false;
        }

        switch (value)
        {
            case Microsoft365TurnEvidence typed:
                evidence = typed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                return TryDeserialize(element.GetRawText(), out evidence);
            case string json when !string.IsNullOrWhiteSpace(json):
                return TryDeserialize(json, out evidence);
            default:
                return false;
        }
    }

    private static bool TryDeserialize(string json, out Microsoft365TurnEvidence? evidence)
    {
        try
        {
            evidence = Microsoft365EvidenceJson.Deserialize(json);
            return true;
        }
        catch (JsonException)
        {
            evidence = null;
            return false;
        }
    }
}
