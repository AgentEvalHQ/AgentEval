// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Tests.Cli;
using AgentEval.Providers;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Xunit;
using SkillsConfig = AgentEval.AgentSkillsEval.AIConfig;

namespace AgentEval.Tests.Samples;

[Collection("EnvVarTests")]
public sealed class AgentSkillsProviderTests
{
    [Fact]
    public void Configuration_BitdeerOnly_UsesItsConfiguredModel()
    {
        using var environment = new ProviderEnvironmentScope(
            ("BITDEER_API_KEY", "synthetic-key"), ("BITDEER_MODEL", "skills-test-model"));

        Assert.True(SkillsConfig.IsConfigured);
        Assert.Equal("skills-test-model", SkillsConfig.ModelDeployment);
    }

    [Theory]
    [InlineData("azure", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT", true)]
    [InlineData("foundry", "FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL", true)]
    [InlineData("bitdeer", "BITDEER_ENDPOINT", "BITDEER_API_KEY", "BITDEER_MODEL", false)]
    [InlineData("openai", "OPENAI_BASE_URL", "OPENAI_API_KEY", "OPENAI_MODEL", false)]
    [InlineData("openai-compatible", "OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_MODEL", false)]
    public void CreateChatClient_SelectedProvider_UsesItsEndpointModelAndProtocol(
        string provider, string endpointVariable, string keyVariable, string modelVariable, bool azureProtocol)
    {
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", provider), (endpointVariable, "https://skills.example/v1"),
            (keyVariable, "synthetic-key"), (modelVariable, "skills-test-model"),
            // A complete competing Azure setup must not override an explicit selection.
            ("AZURE_OPENAI_ENDPOINT", provider == "azure" ? "https://skills.example/v1" : "https://other.example"),
            ("AZURE_OPENAI_API_KEY", "synthetic-key"),
            ("AZURE_OPENAI_DEPLOYMENT", provider == "azure" ? "skills-test-model" : "other-model"));

        Assert.True(SkillsConfig.IsConfigured);
        Assert.Equal(provider, SkillsConfig.Settings.ProviderTag);
        using var client = SkillsConfig.CreateChatClient();
        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("skills-test-model", metadata.DefaultModelId);
        Assert.Equal("skills.example", metadata.ProviderUri?.Host);
        var sdkClient = client.GetService<ChatClient>();
        Assert.NotNull(sdkClient);
        Assert.Same(azureProtocol ? typeof(AzureOpenAIClient).Assembly : typeof(OpenAIClient).Assembly,
            sdkClient.GetType().Assembly);
    }

    [Fact]
    public void Configuration_ExistingAzureEnvironment_PreservesAutoSelectionAndDeployment()
    {
        using var environment = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com"),
            ("AZURE_OPENAI_API_KEY", "synthetic-key"), ("AZURE_OPENAI_DEPLOYMENT", "existing-deployment"),
            ("BITDEER_API_KEY", "synthetic-competing-key"));

        using var client = SkillsConfig.CreateChatClient();
        Assert.Equal(InferenceProvider.AzureOpenAI, SkillsConfig.Settings.Provider);
        Assert.Equal("existing-deployment", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Theory]
    [InlineData("bitdeer")]
    [InlineData("typo-provider")]
    public void CreateChatClient_InvalidExplicitSelection_RefusesCompetingAzure(string provider)
    {
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", provider),
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com"),
            ("AZURE_OPENAI_API_KEY", "synthetic-key"), ("AZURE_OPENAI_DEPLOYMENT", "existing-deployment"));

        Assert.False(SkillsConfig.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => SkillsConfig.ModelDeployment);
        var failure = Assert.Throws<InvalidOperationException>(() => SkillsConfig.CreateChatClient());
        Assert.Contains(provider, failure.Message);
        Assert.DoesNotContain("synthetic-key", failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uri")]
    [InlineData("http://external.example/v1")]
    public void CreateChatClient_MissingOrUnsafeEndpoint_DoesNotConstructAClient(string? endpoint)
    {
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"), ("OPENAI_COMPATIBLE_ENDPOINT", endpoint),
            ("OPENAI_COMPATIBLE_MODEL", "skills-test-model"));

        Assert.False(SkillsConfig.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => SkillsConfig.CreateChatClient());
    }

    [Fact]
    public void CreateChatClient_KeylessLoopback_ConstructsTheConfiguredLocalClient()
    {
        using var environment = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", "http://127.0.0.1:11434/v1"),
            ("OPENAI_COMPATIBLE_MODEL", "local-skills-model"));

        using var client = SkillsConfig.CreateChatClient();
        Assert.Equal("local-skills-model", client.GetService<ChatClientMetadata>()?.DefaultModelId);
        Assert.Equal("127.0.0.1", client.GetService<ChatClientMetadata>()?.ProviderUri?.Host);
    }
}
