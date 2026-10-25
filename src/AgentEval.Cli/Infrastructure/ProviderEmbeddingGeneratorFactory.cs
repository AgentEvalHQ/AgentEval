// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>CLI SDK construction using the shared, SDK-free embedding selection policy.</summary>
internal static class ProviderEmbeddingGeneratorFactory
{
    internal static IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
            throw new InvalidOperationException(settings.Diagnostic ?? "No embedding model is configured.");

        try
        {
            IEmbeddingGenerator<string, Embedding<float>> generator = settings.UsesAzureProtocol
                ? new AzureOpenAIClient(settings.Endpoint!, new AzureKeyCredential(settings.ApiKey!))
                    .GetEmbeddingClient(settings.Model!).AsIEmbeddingGenerator()
                : new OpenAIClient(new ApiKeyCredential(settings.ApiKey!), new OpenAIClientOptions { Endpoint = settings.Endpoint })
                    .GetEmbeddingClient(settings.Model!).AsIEmbeddingGenerator();
            return CliUsageMeter.WrapEmbeddings(generator, settings.Model);
        }
        catch (Exception error)
        {
            // SDK messages and configured URL paths/queries may contain credentials.
            throw new InvalidOperationException(
                $"Failed to construct the {settings.DisplayName} embedding client ({error.GetType().Name}) for " +
                $"endpoint={ProviderChatClientFactory.SafeEndpoint(settings.Endpoint)}. Check the key, endpoint and embedding model.");
        }
    }
}
