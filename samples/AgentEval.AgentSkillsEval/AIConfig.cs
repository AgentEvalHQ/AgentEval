// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using AgentEval.Core;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.AgentSkillsEval;

/// <summary>
/// Chat configuration using the same environment-variable provider selection as the main samples.
/// An explicit <c>AI_INFERENCE_PROVIDER</c> selection never falls back to a different host.
/// </summary>
public static class AIConfig
{
    /// <summary>The provider settings resolved from the current environment.</summary>
    public static InferenceProviderSettings Settings => InferenceProviderEnvironment.Resolve();

    /// <summary>Whether the selected provider has the required configuration.</summary>
    public static bool IsConfigured => Settings.IsConfigured;

    /// <summary>The selected model or Azure deployment name.</summary>
    public static string ModelDeployment => Settings.Model
        ?? throw new InvalidOperationException(Settings.Diagnostic);

    /// <summary>Constructs the selected provider's SDK client without making a model call.</summary>
    public static IChatClient CreateChatClient()
    {
        var settings = Settings;
        if (!settings.IsConfigured)
            throw new InvalidOperationException(settings.Diagnostic ?? "No chat provider is configured.");

        if (settings.UsesAzureProtocol)
        {
            return new AzureOpenAIClient(settings.Endpoint!, new AzureKeyCredential(settings.ApiKey!))
                .GetChatClient(settings.Model!)
                .AsIChatClient().WithMinimalReasoning();
        }

        return new OpenAIClient(new ApiKeyCredential(settings.ApiKey!),
                new OpenAIClientOptions { Endpoint = settings.Endpoint })
            .GetChatClient(settings.Model!)
            .AsIChatClient().WithMinimalReasoning();
    }

    /// <summary>Prints the selected provider's configuration diagnostic without credentials.</summary>
    public static void PrintMissingCredentialsWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(Settings.Diagnostic ?? "No chat provider is configured.");
        Console.WriteLine("Set AI_INFERENCE_PROVIDER to azure, bitdeer, openai, foundry or openai-compatible.");
        Console.WriteLine("See this sample's README for provider variables. No agent run was measured.");
        Console.ResetColor();
        Console.WriteLine();
    }
}
