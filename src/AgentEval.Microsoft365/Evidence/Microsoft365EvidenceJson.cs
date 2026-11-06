// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// The one JSON representation of <see cref="Microsoft365TurnEvidence"/>: camel-case property names, enums as
/// their member names (matching the design's fixture catalog), <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// as arrays (a default array writes <c>[]</c>), <see cref="Observed{T}"/> with its <c>value</c> written only
/// when the state is <see cref="FieldState.Present"/> (so a missing field never shows a substitute value), and
/// every <see cref="JsonElement"/> read back as a detached clone.
/// </summary>
public static class Microsoft365EvidenceJson
{
    /// <summary>The read-only serializer options; share them with any writer that embeds the document (for example the AEF projector).</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Serializes an evidence document to compact JSON.</summary>
    /// <param name="evidence">The document.</param>
    public static string Serialize(Microsoft365TurnEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return JsonSerializer.Serialize(evidence, Options);
    }

    /// <summary>Deserializes an evidence document produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <exception cref="JsonException">The text is not a valid evidence document (including the literal <c>null</c>).</exception>
    public static Microsoft365TurnEvidence Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<Microsoft365TurnEvidence>(json, Options)
            ?? throw new JsonException("The evidence document deserialized to null.");
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ImmutableArrayJsonConverterFactory());
        options.Converters.Add(new ObservedJsonConverterFactory());
        options.Converters.Add(new DetachedJsonElementConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
