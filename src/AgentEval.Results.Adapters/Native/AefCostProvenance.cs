// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Models;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Preserves the complete rate card outside AEF's bounded usage summary.</summary>
internal static class AefCostProvenance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static JsonObject? Attach(JsonObject? extensions, TokenCostEstimate? estimate)
    {
        if (estimate is null) return extensions;
        var result = extensions?.DeepClone().AsObject() ?? new JsonObject();
        result["agenteval.costEstimate"] = JsonSerializer.SerializeToNode(estimate, JsonOptions);
        return result;
    }
}
