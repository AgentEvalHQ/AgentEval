// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Providers;

/// <summary>
/// The embedding model for the selected provider, read from the environment once. Carries no SDK types, so the
/// selection rules are tested without a network or a client library; the host builds its SDK client from them.
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

    /// <summary>Never prints the key or endpoint, which may contain credentials.</summary>
    public override string ToString() =>
        $"{nameof(EmbeddingProviderSettings)} {{ Provider = {Provider}, ProviderTag = {ProviderTag}, Endpoint = {(Endpoint is null ? "null" : "[configured]")}, " +
        $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")}, Model = {Model}, ModelVariable = {ModelVariable}, " +
        $"ModelIsDefault = {ModelIsDefault}, Diagnostic = {Diagnostic} }}";
}
