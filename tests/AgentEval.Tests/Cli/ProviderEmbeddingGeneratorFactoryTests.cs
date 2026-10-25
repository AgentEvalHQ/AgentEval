// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Cli.Infrastructure;
using AgentEval.Providers;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

public class ProviderEmbeddingGeneratorFactoryTests
{
    [Theory]
    [InlineData(InferenceProvider.AzureOpenAI)]
    [InlineData(InferenceProvider.Foundry)]
    [InlineData(InferenceProvider.Bitdeer)]
    [InlineData(InferenceProvider.OpenAI)]
    [InlineData(InferenceProvider.OpenAICompatible)]
    public void Create_KnownProvider_ConstructsItsConfiguredEmbeddingModelWithoutRequests(InferenceProvider provider)
    {
        using var generator = ProviderEmbeddingGeneratorFactory.Create(new(provider, provider.ToString(), "Synthetic",
            new Uri("https://example.test/"), "synthetic-key", "embedding-model", "EMBEDDING_MODEL", false, null));

        Assert.Equal("embedding-model", generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId);
    }

    [Fact]
    public void Create_MissingConfiguration_PreservesActionableDiagnostic()
    {
        var settings = new EmbeddingProviderSettings(InferenceProvider.None, "none", "None", null, null, null,
            null, false, "Set the selected provider's embedding model.");

        var error = Assert.Throws<InvalidOperationException>(() => ProviderEmbeddingGeneratorFactory.Create(settings));

        Assert.Equal(settings.Diagnostic, error.Message);
    }

    [Fact]
    public void Create_InvalidSdkCredential_RedactsEndpointAndDoesNotRetainSdkException()
    {
        var settings = new EmbeddingProviderSettings(InferenceProvider.AzureOpenAI, "azure", "Synthetic",
            new Uri("https://url-user:url-password@example.test/path-secret?token=query-secret#fragment-secret"),
            null, "embedding-model", "EMBEDDING_MODEL", false, null);

        var error = Assert.Throws<InvalidOperationException>(() => ProviderEmbeddingGeneratorFactory.Create(settings));

        Assert.Contains("ArgumentNullException", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        foreach (var secret in new[] { "url-user", "url-password", "path-secret", "query-secret", "fragment-secret" })
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }
}
