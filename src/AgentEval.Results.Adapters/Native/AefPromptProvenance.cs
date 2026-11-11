// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Prompts;

namespace AgentEval.Results.Adapters.Native;

internal static class AefPromptProvenance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static JsonObject? Attach(JsonObject? extensions, JudgePromptProvenance? prompt, bool includeContent = false)
    {
        if (prompt is null) return extensions;
        var result = extensions?.DeepClone().AsObject() ?? new JsonObject();
        result["agenteval.judgePrompt"] = JsonSerializer.SerializeToNode(includeContent ? prompt : prompt with { Content = null }, JsonOptions);
        return result;
    }
}
