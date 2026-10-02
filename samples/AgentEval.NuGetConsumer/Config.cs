// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Which chat provider the sample talks to, resolved exactly as the AgentEval CLI and samples resolve it, through
/// <see cref="InferenceProviderEnvironment"/> from the published package.
/// <code>
/// AI_INFERENCE_PROVIDER = bitdeer | openai | foundry | azure | openai-compatible
///   bitdeer   BITDEER_API_KEY                                   (BITDEER_ENDPOINT, BITDEER_MODEL optional)
///   openai    OPENAI_API_KEY                                    (OPENAI_BASE_URL, OPENAI_MODEL optional)
///   azure     AZURE_OPENAI_ENDPOINT + _API_KEY + _DEPLOYMENT
///   foundry   FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL
/// </code>
/// An explicit selector wins; when it is unset, the first provider with complete credentials is used. An explicit
/// selector whose variables are missing is reported as not configured, never silently replaced by another host.
/// </summary>
public static class Config
{
    private static readonly Lazy<InferenceProviderSettings> s_settings = new(() => InferenceProviderEnvironment.Resolve());

    /// <summary>The resolved provider settings. Never log <see cref="InferenceProviderSettings.ApiKey"/>.</summary>
    public static InferenceProviderSettings Settings => s_settings.Value;

    /// <summary>True when a provider with complete credentials is selected.</summary>
    public static bool IsConfigured => Settings.IsConfigured;

    /// <summary>Why nothing is configured, when nothing is (for example an explicit provider with a missing key).</summary>
    public static string? Diagnostic => Settings.Diagnostic;

    /// <summary>The provider's display name, for example "Bitdeer AI Model Studio".</summary>
    public static string ProviderName => Settings.DisplayName;

    /// <summary>The provider endpoint.</summary>
    public static Uri Endpoint => Settings.Endpoint
        ?? throw new InvalidOperationException(Diagnostic ?? "No inference provider is configured.");

    /// <summary>The API key. Never print it.</summary>
    public static string ApiKey => Settings.ApiKey
        ?? throw new InvalidOperationException(Diagnostic ?? "No inference provider is configured.");

    /// <summary>True for Azure OpenAI and Foundry, which share the Azure protocol; false for OpenAI-compatible hosts.</summary>
    public static bool UsesAzureProtocol => Settings.UsesAzureProtocol;

    /// <summary>
    /// Primary model (or Azure deployment) name. With no provider configured (mock mode) it is the label the sample
    /// has always used, so mock output and its cost estimates are unchanged; no call is made with it.
    /// </summary>
    public static string Model => Settings.Model ?? InferenceProviderEnvironment.AzureDefaultDeployment;

    /// <summary>Secondary model for comparison testing; the provider's <c>*_MODEL_2</c>, else the primary model.</summary>
    public static string SecondaryModel => Settings.IsConfigured
        ? Settings.SecondaryModel ?? Model
        : InferenceProviderEnvironment.AzureDefaultSecondaryDeployment;

    /// <summary>
    /// Builds a chat client for <paramref name="model"/> (default <see cref="Model"/>) on the selected provider:
    /// the Azure protocol for Azure OpenAI and Foundry, the OpenAI protocol at the provider's endpoint otherwise.
    /// </summary>
    public static IChatClient CreateChatClient(string? model = null)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Diagnostic ?? "No inference provider is configured.");

        var resolved = model ?? Model;
        if (UsesAzureProtocol)
        {
            return new AzureOpenAIClient(Endpoint, new AzureKeyCredential(ApiKey))
                .GetChatClient(resolved)
                .AsIChatClient();
        }

        return new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions { Endpoint = Endpoint })
            .GetChatClient(resolved)
            .AsIChatClient();
    }

    /// <summary>The message to show when no provider is configured.</summary>
    public static string NotConfiguredMessage =>
        (Diagnostic is null ? "No inference provider is configured. " : Diagnostic + " ") +
        "Set AI_INFERENCE_PROVIDER (bitdeer, openai, foundry, azure or openai-compatible) and that provider's " +
        "variables, for example BITDEER_API_KEY.";
}
