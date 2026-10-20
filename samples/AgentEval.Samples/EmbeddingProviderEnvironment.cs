// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Providers;

namespace AgentEval.Samples;

/// <summary>
/// The embedding model for the selected provider, read from the environment once. Carries no SDK types, so the
/// selection rules are tested without a network or a client library; <c>AIConfig.CreateEmbeddingGenerator</c>
/// builds the client from it.
/// </summary>
/// <param name="Provider">The chat provider the embeddings follow, or <see cref="InferenceProvider.None"/> when no chat provider is configured.</param>
/// <param name="ProviderTag">The chat provider's tag (<c>bitdeer</c>, <c>azure</c>, ...).</param>
/// <param name="DisplayName">The chat provider's display name, for banners.</param>
/// <param name="Endpoint">The endpoint the chat client uses; embeddings go to the same host.</param>
/// <param name="ApiKey">The key the chat client uses. Never log it.</param>
/// <param name="Model">The embedding model, or on the Azure-protocol hosts the embedding deployment's name.</param>
/// <param name="ModelVariable">The variable that names the model for this provider.</param>
/// <param name="ModelIsDefault">True when <paramref name="ModelVariable"/> is unset and the provider's default was taken.</param>
/// <param name="Diagnostic">Why no embedding model is configured, when none is.</param>
public sealed record EmbeddingProviderSettings(
    InferenceProvider Provider,
    string ProviderTag,
    string DisplayName,
    Uri? Endpoint,
    string? ApiKey,
    string? Model,
    string? ModelVariable,
    bool ModelIsDefault,
    string? Diagnostic)
{
    /// <summary>True when the selected provider has an embedding model, named or by default.</summary>
    public bool IsConfigured => Provider != InferenceProvider.None && Model is not null;

    /// <summary>True for the hosts that speak the Azure OpenAI protocol, where <see cref="Model"/> is a deployment name.</summary>
    public bool UsesAzureProtocol => Provider is InferenceProvider.AzureOpenAI or InferenceProvider.Foundry;

    /// <summary><c>model@provider</c>: a similarity score depends on the embedding model, so the model belongs with the number.</summary>
    public string ModelIdentity => $"{Model ?? "?"}@{ProviderTag}";

    /// <summary>Never prints the key; a positional record's generated <c>ToString()</c> would.</summary>
    public override string ToString() =>
        $"{nameof(EmbeddingProviderSettings)} {{ Provider = {Provider}, ProviderTag = {ProviderTag}, Endpoint = {Endpoint}, " +
        $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")}, Model = {Model}, ModelVariable = {ModelVariable}, " +
        $"ModelIsDefault = {ModelIsDefault}, Diagnostic = {Diagnostic} }}";
}

/// <summary>
/// Resolves <see cref="EmbeddingProviderSettings"/>: embeddings follow the chat provider that
/// <c>AI_INFERENCE_PROVIDER</c> selects (<see cref="InferenceProviderEnvironment"/>), on the same endpoint with the
/// same key, so a sample never sends its embeddings to a host the user did not choose.
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
/// guessed instrument: similarity scores differ by embedding model, so the model must be named. This lives in the
/// samples because only they build an embedding client from the environment; it belongs next to
/// <see cref="InferenceProviderEnvironment"/> in Core when the CLI or a host needs the same.
/// </remarks>
public static class EmbeddingProviderEnvironment
{
    public const string BitdeerDefaultModel = "BAAI/bge-m3";
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
