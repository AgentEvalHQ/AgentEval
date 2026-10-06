// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ClientModel;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.HealthcareSafetyPack;

/// <summary>
/// The model provider this sample talks to, resolved as the AgentEval CLI and the other samples resolve it, through
/// <see cref="InferenceProviderEnvironment"/>:
/// <code>
/// AI_INFERENCE_PROVIDER = bitdeer | openai | foundry | azure | openai-compatible
///   bitdeer   BITDEER_API_KEY                                   (BITDEER_ENDPOINT, BITDEER_MODEL optional)
///   openai    OPENAI_API_KEY                                    (OPENAI_BASE_URL, OPENAI_MODEL optional)
///   azure     AZURE_OPENAI_ENDPOINT + _API_KEY + _DEPLOYMENT
///   foundry   FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL
/// </code>
/// An explicit selector wins; an explicit selector whose variables are missing is reported as not configured, never
/// replaced by another provider.
/// </summary>
public static class Config
{
    private static readonly Lazy<InferenceProviderSettings> s_settings = new(() => InferenceProviderEnvironment.Resolve());

    private static InferenceProviderSettings Settings => s_settings.Value;

    /// <summary>True when a provider with complete credentials is selected.</summary>
    public static bool IsConfigured => Settings.IsConfigured;

    /// <summary>The provider's display name, for example "Bitdeer AI Model Studio".</summary>
    public static string ProviderName => Settings.DisplayName;

    /// <summary>The model (or Azure deployment) both the agent and the judges use.</summary>
    public static string Model => Settings.Model ?? InferenceProviderEnvironment.AzureDefaultDeployment;

    /// <summary>Builds a chat client for <see cref="Model"/> on the selected provider.</summary>
    public static IChatClient CreateChatClient()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(NotConfiguredMessage);

        var endpoint = Settings.Endpoint ?? throw new InvalidOperationException(NotConfiguredMessage);
        var apiKey = Settings.ApiKey ?? throw new InvalidOperationException(NotConfiguredMessage);
        if (Settings.UsesAzureProtocol)
        {
            return new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey))
                .GetChatClient(Model)
                .AsIChatClient();
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint })
            .GetChatClient(Model)
            .AsIChatClient();
    }

    /// <summary>The message to show when no provider is configured.</summary>
    public static string NotConfiguredMessage =>
        (Settings.Diagnostic is null ? "No inference provider is configured. " : Settings.Diagnostic + " ") +
        "Set AI_INFERENCE_PROVIDER (bitdeer, openai, foundry, azure or openai-compatible) and that provider's " +
        "variables, for example BITDEER_API_KEY.";
}
