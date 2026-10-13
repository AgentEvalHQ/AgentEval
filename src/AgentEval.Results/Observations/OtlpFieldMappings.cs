// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Results.Observations;

internal static class OtlpFieldMappings
{
    internal sealed record Mapping(string Target, string Profile, bool Count = false);
    private const string GenAi = "otel-genai-1.37.0-fields/1";
    private const string Legacy = "genai-legacy-aliases/1";
    private const string OpenInference = "openinference-2026-10-14/1";
    private const string Vercel = "vercel-ai-legacy-2026-10-14/1";
    internal static readonly ImmutableDictionary<string, Mapping> SpanMappings = new Dictionary<string, Mapping>(StringComparer.Ordinal)
    {
        ["gen_ai.operation.name"] = new("operation", GenAi),
        ["gen_ai.provider.name"] = new("provider", GenAi),
        ["gen_ai.system"] = new("system", Legacy),
        ["gen_ai.request.model"] = new("model.request", GenAi),
        ["gen_ai.response.model"] = new("model.response", GenAi),
        ["gen_ai.conversation.id"] = new("session.id", GenAi),
        ["gen_ai.agent.id"] = new("agent.id", GenAi),
        ["gen_ai.agent.name"] = new("agent.name", GenAi),
        ["gen_ai.tool.name"] = new("tool.name", GenAi),
        ["gen_ai.tool.call.id"] = new("tool.call.id", GenAi),
        ["gen_ai.usage.input_tokens"] = new("tokens.input", GenAi, true),
        ["gen_ai.usage.output_tokens"] = new("tokens.output", GenAi, true),
        ["gen_ai.usage.prompt_tokens"] = new("tokens.input", Legacy, true),
        ["gen_ai.usage.completion_tokens"] = new("tokens.output", Legacy, true),
        ["gen_ai.usage.total_tokens"] = new("tokens.total", Legacy, true),
        ["gen_ai.session.id"] = new("session.id", "eino-session-alias/1"),
        ["openinference.span.kind"] = new("span.role", OpenInference),
        ["llm.provider"] = new("provider", OpenInference),
        ["llm.system"] = new("system", OpenInference),
        ["llm.model_name"] = new("model.reported", OpenInference),
        ["llm.request.model_name"] = new("model.request", OpenInference),
        ["llm.response.model_name"] = new("model.response", OpenInference),
        ["embedding.model_name"] = new("embedding.model", OpenInference),
        ["llm.token_count.prompt"] = new("tokens.input", OpenInference, true),
        ["llm.token_count.completion"] = new("tokens.output", OpenInference, true),
        ["llm.token_count.total"] = new("tokens.total", OpenInference, true),
        ["session.id"] = new("session.id", OpenInference),
        ["tool.name"] = new("tool.name", OpenInference),
        ["ai.operationId"] = new("operation.native", Vercel),
        ["ai.model.id"] = new("model.request", Vercel),
        ["ai.model.provider"] = new("provider", Vercel),
        ["ai.response.model"] = new("model.response", Vercel),
        ["ai.usage.promptTokens"] = new("tokens.input", Vercel, true),
        ["ai.usage.completionTokens"] = new("tokens.output", Vercel, true),
        ["ai.toolCall.name"] = new("tool.name", Vercel),
        ["ai.toolCall.id"] = new("tool.call.id", Vercel)
    }.ToImmutableDictionary(StringComparer.Ordinal);
    internal static readonly ImmutableDictionary<string, Mapping> ResourceMappings = new Dictionary<string, Mapping>(StringComparer.Ordinal)
    {
        ["service.name"] = new("service.name", "otel-resource-fields/1"),
        ["service.namespace"] = new("service.namespace", "otel-resource-fields/1"),
        ["service.instance.id"] = new("service.instance.id", "otel-resource-fields/1")
    }.ToImmutableDictionary(StringComparer.Ordinal);

}
