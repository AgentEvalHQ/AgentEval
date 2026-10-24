// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Providers;

/// <summary>
/// Resolves <see cref="EmbeddingProviderSettings"/>: embeddings follow the chat provider that
/// <c>AI_INFERENCE_PROVIDER</c> selects (<see cref="InferenceProviderEnvironment"/>), on the same endpoint with the
/// same key, so a caller never sends its embeddings to a host the user did not choose.
/// </summary>
/// <remarks>
/// <code>
/// bitdeer             BITDEER_EMBEDDING_MODEL              default BAAI/bge-m3
/// openai              OPENAI_EMBEDDING_MODEL               default text-embedding-3-small
/// azure               AZURE_OPENAI_EMBEDDING_DEPLOYMENT    required: the name of an embedding deployment on the resource
/// foundry             FOUNDRY_EMBEDDING_MODEL              required: the name of an embedding deployment on the resource
/// openai-compatible   OPENAI_COMPATIBLE_EMBEDDING_MODEL    required: the host serves whatever it serves
/// </code>
/// A default exists only where the provider's catalogue makes one obvious. Elsewhere a guessed model would be a
/// guessed instrument: similarity scores differ by embedding model, so the model must be named. The CLI, samples and hosts share these selection rules without depending on a client SDK.
/// </remarks>
public static class EmbeddingProviderEnvironment
{
    /// <summary>The default embedding model on Bitdeer's canonical endpoint.</summary>
    public const string BitdeerDefaultModel = "BAAI/bge-m3";
    /// <summary>The default embedding model on OpenAI's canonical endpoint.</summary>
    public const string OpenAIDefaultModel = "text-embedding-3-small";

    /// <summary>The variable that names the embedding model for <paramref name="provider"/>; <see langword="null"/> for none.</summary>
    public static string? ModelVariableOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.AzureOpenAI => "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
        InferenceProvider.Bitdeer => "BITDEER_EMBEDDING_MODEL",
        InferenceProvider.OpenAI => "OPENAI_EMBEDDING_MODEL",
        InferenceProvider.Foundry => "FOUNDRY_EMBEDDING_MODEL",
        InferenceProvider.OpenAICompatible => "OPENAI_COMPATIBLE_EMBEDDING_MODEL",
        _ => null,
    };

    /// <summary>The model taken when <see cref="ModelVariableOf"/> is unset; <see langword="null"/> where the model must be named.</summary>
    public static string? DefaultModelOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Bitdeer => BitdeerDefaultModel,
        InferenceProvider.OpenAI => OpenAIDefaultModel,
        _ => null,
    };

    // The endpoint a provider's default embedding model is known to be served at. Behind BITDEER_ENDPOINT or
    // OPENAI_BASE_URL pointed elsewhere (a proxy, a gateway, another host) the catalogue is unknown, and a default there
    // would be a guessed instrument: the model must be named.
    private static string? DefaultEndpointOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Bitdeer => InferenceProviderEnvironment.BitdeerDefaultEndpoint,
        InferenceProvider.OpenAI => InferenceProviderEnvironment.OpenAIDefaultEndpoint,
        _ => null,
    };

    private static bool IsDefaultEndpoint(InferenceProvider provider, Uri? endpoint) =>
        DefaultEndpointOf(provider) is { } known && endpoint is not null
        && string.Equals(endpoint.AbsoluteUri.TrimEnd('/'), known.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every variable this resolver reads, beyond the chat provider's own.</summary>
    public static readonly IReadOnlyList<string> AllEmbeddingVariables =
    [
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "BITDEER_EMBEDDING_MODEL", "OPENAI_EMBEDDING_MODEL",
        "FOUNDRY_EMBEDDING_MODEL", "OPENAI_COMPATIBLE_EMBEDDING_MODEL",
    ];

    /// <summary>Resolves the embedding settings for <paramref name="chat"/>'s provider.</summary>
    /// <param name="chat">The resolved chat provider (<see cref="InferenceProviderEnvironment.Resolve()"/>).</param>
    /// <param name="getEnvironmentVariable">Returns a variable's value or <see langword="null"/> (tests pass a dictionary).</param>
    public static EmbeddingProviderSettings Resolve(InferenceProviderSettings chat, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!chat.IsConfigured)
        {
            return NotConfigured(chat, null,
                $"Embeddings follow the chat provider, and none is configured. {chat.Diagnostic ?? ""}".TrimEnd());
        }

        var variable = ModelVariableOf(chat.Provider)!;
        var named = getEnvironmentVariable(variable) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var model = named ?? (IsDefaultEndpoint(chat.Provider, chat.Endpoint) ? DefaultModelOf(chat.Provider) : null);
        if (model is null)
        {
            return NotConfigured(chat, variable, DefaultModelOf(chat.Provider) is { } usual
                ? $"Embeddings follow the chat provider ({chat.ProviderTag}), whose endpoint is not its default one, so " +
                  $"its usual embedding model ({usual}) may not be served there: set {variable} to a model the endpoint serves."
                : $"Embeddings follow the chat provider ({chat.ProviderTag}), which has no default embedding model: " +
                  $"set {variable} to {WhatToName(chat.Provider)}.");
        }

        return new(chat.Provider, chat.ProviderTag, chat.DisplayName, chat.Endpoint, chat.ApiKey, model, variable,
            ModelIsDefault: named is null, Diagnostic: null);
    }

    private static string WhatToName(InferenceProvider provider) => provider switch
    {
        InferenceProvider.AzureOpenAI => "the name of an embedding deployment on the Azure OpenAI resource (e.g. one serving text-embedding-3-small)",
        InferenceProvider.Foundry => "the name of an embedding deployment on the Foundry resource",
        _ => "an embedding model the endpoint serves (on Ollama, e.g. bge-m3 or nomic-embed-text)",
    };

    // Keeps the provider, so a banner can say "azure has no embedding deployment" rather than "nothing configured";
    // drops the endpoint and key, so nothing can build a client from a not-configured result.
    private static EmbeddingProviderSettings NotConfigured(InferenceProviderSettings chat, string? variable, string diagnostic) =>
        new(chat.Provider, chat.ProviderTag, chat.DisplayName, null, null, null, variable, false, diagnostic);
}
