// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.
// Coexists with AzureChatAgentFactory (Phase-1 T0.2): EndpointFactory is the EXPLICIT
// path (--endpoint --model --api-key / --azure --deployment-name --api-key),
// AzureChatAgentFactory is the AZURE_OPENAI_* env-var convention (used by --from-env).

using System.ClientModel;
using System.ClientModel.Primitives;
using AgentEval.Providers;
using AgentEval.Results;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using AgentEval.Core;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// Creates <see cref="IChatClient"/> instances from CLI endpoint arguments.
/// Supports any OpenAI-compatible endpoint and Azure OpenAI.
/// </summary>
internal static class EndpointFactory
{
    /// <summary>
    /// Creates the runner's endpoint client using only the purpose-scoped key. Each chat invocation makes
    /// at most one SDK attempt, so transport retries cannot bypass the runner's per-role call budget.
    /// </summary>
    /// <remarks>
    /// OpenAI-compatible local servers may be keyless; Azure requires an explicit scoped key.
    /// This path deliberately does not use the interactive CLI's environment-variable fallbacks.
    /// </remarks>
    internal static IChatClient CreateForRunner(string endpoint, string model, string? apiKey, bool azure = false)
    {
        var uri = ValidateRunnerEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(model))
            throw new AefInputException("Runner model must be specified.");
        if (apiKey is not null && string.IsNullOrWhiteSpace(apiKey))
            throw new AefInputException("Runner credential must be non-empty when supplied.");

        if (azure)
        {
            if (apiKey is null)
                throw new AefInputException("Runner Azure endpoint requires a purpose-scoped credential.");
            var options = new AzureOpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0) };
            return new AzureOpenAIClient(uri, new AzureKeyCredential(apiKey), options)
                .GetChatClient(model).AsIChatClient();
        }

        // The target resolves reasoning during budgeted readiness, then freezes it for cases.
        // Keep this transport to one SDK attempt; no reasoning fallback may hide inside accounting.
        var openAIOptions = new OpenAIClientOptions { Endpoint = uri, RetryPolicy = new ClientRetryPolicy(0) };
        return new OpenAIClient(new ApiKeyCredential(apiKey ?? "no-key-needed"), openAIOptions)
            .GetChatClient(model).AsIChatClient();
    }

    internal static Uri ValidateRunnerEndpoint(string? endpoint)
    {
        if (!InferenceProviderEnvironment.TryValidateEndpoint(endpoint, out var uri, out _)
            || uri is null || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AefInputException("Runner endpoints require HTTPS (HTTP is allowed only on loopback), without user info, query or fragment.");
        return uri;
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for any OpenAI-compatible endpoint.
    /// Works with OpenAI, Ollama, Groq, vLLM, LM Studio, Together.ai, Fireworks, Mistral, etc.
    /// </summary>
    /// <param name="endpoint">The API endpoint URL.</param>
    /// <param name="model">The model/deployment name.</param>
    /// <param name="apiKey">Optional API key; falls back to the OPENAI_API_KEY env var, then "no-key-needed"
    /// (some local providers like Ollama don't require one).</param>
    /// <returns>An <see cref="IChatClient"/> ready for evaluation.</returns>
    public static IChatClient CreateOpenAICompatible(string endpoint, string model, string? apiKey)
    {
        var credential = new ApiKeyCredential(ResolveOpenAIKey(apiKey));
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(model).AsIChatClient().WithMinimalReasoning();
    }

    /// <summary>M14: resolves the OpenAI-compatible key — explicit <paramref name="apiKey"/> wins, else the
    /// <c>OPENAI_API_KEY</c> env var (the documented fallback the help promises), else the no-key-needed sentinel
    /// for keyless local providers. Mirrors the Azure path's AZURE_OPENAI_API_KEY behavior.</summary>
    internal static string ResolveOpenAIKey(string? apiKey)
        => apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "no-key-needed";

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for Azure OpenAI.
    /// The endpoint and deployment name are required; the API key can be provided directly or via the AZURE_OPENAI_API_KEY env var.
    /// </summary>
    /// <param name="endpoint">The Azure OpenAI resource endpoint URL (required).</param>
    /// <param name="deploymentName">The Azure OpenAI deployment name (required).</param>
    /// <param name="apiKey">Optional API key (falls back to AZURE_OPENAI_API_KEY env var).</param>
    /// <returns>An <see cref="IChatClient"/> for Azure OpenAI.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required endpoint or API key is missing.</exception>
    public static IChatClient CreateAzure(string? endpoint, string deploymentName, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(
                "Azure endpoint required. Specify --endpoint <url> with your Azure OpenAI resource URL.");

        var resolvedKey = apiKey
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "Azure API key required. Set --api-key or AZURE_OPENAI_API_KEY env var.");

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(resolvedKey));

        return client.GetChatClient(deploymentName).AsIChatClient().WithMinimalReasoning();
    }
}
