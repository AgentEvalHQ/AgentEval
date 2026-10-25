// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.Providers;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatClient = OpenAI.Chat.ChatClient;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>
/// The demo and its evaluation run on whichever provider <c>AI_INFERENCE_PROVIDER</c> selects, through Core's
/// resolver, and fall back to the scripted model only when told to or when nothing live is configured. Every case
/// reads a dictionary rather than the process environment, and none opens a connection: building a client is
/// local, and nothing here sends a request.
/// </summary>
public sealed class ModelSelectionTests
{
    private const string FakeKey = "test-key-not-a-real-credential-7f3a";

    private static readonly Assembly AzureSdk = typeof(AzureOpenAIClient).Assembly;
    private static readonly Assembly OpenAISdk = typeof(OpenAIClient).Assembly;

    private static readonly (string Name, string Value)[] AzureTrio =
    [
        ("AZURE_OPENAI_ENDPOINT", "https://example-resource.openai.azure.com/"),
        ("AZURE_OPENAI_API_KEY", FakeKey),
        ("AZURE_OPENAI_DEPLOYMENT", "gpt-5.5"),
    ];

    [Fact]
    public void Bitdeer_WithOnlyItsKey_IsLiveOnGlm_AndTheBannerNamesProviderAndModelButNotTheKey()
    {
        var selection = ModelSelection.Resolve(
            [],
            forceOffline: false,
            Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", FakeKey)));

        Assert.True(selection.IsLive);
        Assert.Equal(InferenceProvider.Bitdeer, selection.Settings.Provider);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, selection.Model);

        var banner = selection.Describe();
        Assert.Contains(
            "Model: live Bitdeer AI Model Studio, model 'zai-org/GLM-5.3-Flash'", banner, StringComparison.Ordinal);
        Assert.Contains("selected by AI_INFERENCE_PROVIDER", banner, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, banner, StringComparison.Ordinal);
        Assert.DoesNotContain("SCRIPTED", banner, StringComparison.Ordinal);
        Assert.Equal("Bitdeer AI Model Studio 'zai-org/GLM-5.3-Flash'", selection.Label);
    }

    [Fact]
    public void Bitdeer_BuildsTheOpenAIProtocolClient_OnBitdeersEndpoint_ForTheResolvedModel()
    {
        var selection = ModelSelection.Resolve(
            [],
            forceOffline: false,
            Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", FakeKey)));

        using var client = selection.CreateChatClient();
        var metadata = client.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("zai-org/GLM-5.3-Flash", metadata.DefaultModelId);
        Assert.Equal("api-inference.bitdeer.ai", metadata.ProviderUri?.Host);
        // Both SDKs report the same metadata for the same endpoint and model; only the inner client says which
        // one was built.
        Assert.Same(OpenAISdk, SdkOf(client));
    }

    /// <summary>
    /// Which SDK builds each provider's client. The Azure-protocol hosts (Azure OpenAI and a Foundry resource) get
    /// the Azure SDK, which routes to <c>/openai/deployments/…</c>; every other host gets the OpenAI SDK at its own
    /// endpoint. Swapping the two branches would send each to a path the other host does not serve.
    /// </summary>
    [Theory]
    [InlineData("azure", true, "example-resource.openai.azure.com")]
    [InlineData("foundry", true, "example-foundry.openai.azure.com")]
    [InlineData("bitdeer", false, "api-inference.bitdeer.ai")]
    [InlineData("openai", false, "api.openai.com")]
    [InlineData("openai-compatible", false, "localhost")]
    public void EachProvider_IsBuiltWithTheSdkThatSpeaksItsProtocol(string provider, bool azureSdk, string host)
    {
        (string, string)[] variables = provider switch
        {
            "azure" => AzureTrio,
            "foundry" =>
            [
                ("FOUNDRY_ENDPOINT", "https://example-foundry.openai.azure.com/"),
                ("FOUNDRY_API_KEY", FakeKey),
                ("FOUNDRY_MODEL", "gpt-5.5"),
            ],
            "bitdeer" => [("BITDEER_API_KEY", FakeKey)],
            "openai" => [("OPENAI_API_KEY", FakeKey)],
            "openai-compatible" =>
            [
                ("OPENAI_COMPATIBLE_ENDPOINT", "http://localhost:11434/v1"),
                ("OPENAI_COMPATIBLE_MODEL", "llama3.2"),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        var selection = ModelSelection.Resolve(
            [], forceOffline: false, Env([.. variables, ("AI_INFERENCE_PROVIDER", provider)]));

        Assert.True(selection.IsLive);
        Assert.Equal(azureSdk, selection.Settings.UsesAzureProtocol);

        using var client = selection.CreateChatClient();
        Assert.Same(azureSdk ? AzureSdk : OpenAISdk, SdkOf(client));

        Assert.Equal(host, client.GetService<ChatClientMetadata>()?.ProviderUri?.Host);
        Assert.Equal(selection.Model, client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public void Offline_BeatsAConfiguredProvider_AndSaysItIsScripted()
    {
        var selection = ModelSelection.Resolve(
            ["--offline"],
            forceOffline: true,
            Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", FakeKey)));

        Assert.False(selection.IsLive);
        Assert.Contains("Model: SCRIPTED offline provider (--offline).", selection.Describe(), StringComparison.Ordinal);
        Assert.Equal("scripted (offline, deterministic)", selection.Label);
        Assert.Throws<InvalidOperationException>(() => selection.CreateChatClient());
    }

    [Fact]
    public void NothingConfigured_RunsScripted_AndSaysHowToGoLive()
    {
        var selection = ModelSelection.Resolve([], forceOffline: false, Env());

        Assert.False(selection.IsLive);
        Assert.Null(selection.Model);
        var banner = selection.Describe();
        Assert.StartsWith("Model: SCRIPTED offline provider.", banner, StringComparison.Ordinal);
        Assert.Contains("BITDEER_API_KEY", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("No live provider was used", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitSelectorMissingItsKey_FailsClosed_RatherThanFallingBackToAzure()
    {
        var selection = ModelSelection.Resolve(
            [],
            forceOffline: false,
            Env([.. AzureTrio, ("AI_INFERENCE_PROVIDER", "bitdeer")]));

        Assert.False(selection.IsLive);
        Assert.Equal(InferenceProvider.None, selection.Settings.Provider);
        var banner = selection.Describe();
        Assert.Contains("No live provider was used", banner, StringComparison.Ordinal);
        Assert.Contains("BITDEER_API_KEY", banner, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, banner, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--all" }, "gpt-5.5")]
    [InlineData(new[] { "--all", "--model", "gpt-5-mini" }, "gpt-5-mini")]
    [InlineData(new[] { "--all", "--deployment", "gpt-5-mini" }, "gpt-5-mini")]
    [InlineData(new[] { "--deployment", "ignored", "--model", "gpt-5-chat" }, "gpt-5-chat")]
    public void ModelOverride_ReplacesTheResolvedModel_AndDeploymentStillWorksAsAnAlias(string[] args, string expected)
    {
        var selection = ModelSelection.Resolve(args, forceOffline: false, Env(AzureTrio));

        Assert.True(selection.IsLive);
        Assert.Equal(InferenceProvider.AzureOpenAI, selection.Settings.Provider);
        Assert.Equal(expected, selection.Model);
        Assert.Contains(
            $"Model: live Azure OpenAI, model '{expected}' (auto-detected", selection.Describe(), StringComparison.Ordinal);

        using var client = selection.CreateChatClient();
        Assert.Equal(expected, client.GetService<ChatClientMetadata>()?.DefaultModelId);
        Assert.Same(AzureSdk, SdkOf(client));
    }

    /// <summary>
    /// The Azure-only demo read <c>--deployment NAME ?? AZURE_OPENAI_DEPLOYMENT</c>, so an endpoint and a key plus
    /// the flag ran live. That contract still holds: the option completes the Azure trio it is the third part of.
    /// </summary>
    [Theory]
    [InlineData(null, "--deployment")]
    [InlineData(null, "--model")]
    [InlineData("azure", "--deployment")]
    [InlineData("azure", "--model")]
    public void DeploymentOption_CompletesAnAzureSetupThatHasOnlyEndpointAndKey(string? selector, string option)
    {
        (string, string)[] variables =
        [
            ("AZURE_OPENAI_ENDPOINT", "https://example-resource.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", FakeKey),
        ];
        if (selector is not null)
        {
            variables = [.. variables, ("AI_INFERENCE_PROVIDER", selector)];
        }

        var selection = ModelSelection.Resolve([option, "gpt-5.5"], forceOffline: false, Env(variables));

        Assert.True(selection.IsLive);
        Assert.Equal(InferenceProvider.AzureOpenAI, selection.Settings.Provider);
        Assert.Equal("gpt-5.5", selection.Model);
        Assert.Contains("Model: live Azure OpenAI, model 'gpt-5.5'", selection.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, selection.Describe(), StringComparison.Ordinal);

        using var client = selection.CreateChatClient();
        Assert.Same(AzureSdk, SdkOf(client));
        Assert.Equal("gpt-5.5", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    /// <summary>
    /// The other direction: the option is a model name, never a credential, an endpoint or a provider choice. It
    /// completes only the Azure deployment, and only when nothing resolves without it.
    /// </summary>
    [Theory]
    // An endpoint without a key, and a key without an endpoint: the flag supplies neither.
    [InlineData(new[] { "AZURE_OPENAI_ENDPOINT=https://example-resource.openai.azure.com/" }, "AZURE_OPENAI_API_KEY")]
    [InlineData(new[] { "AZURE_OPENAI_API_KEY=" + FakeKey }, "AZURE_OPENAI_ENDPOINT")]
    // A selector naming a provider whose key is absent is not satisfied by a complete-able Azure setup.
    [InlineData(
        new[]
        {
            "AI_INFERENCE_PROVIDER=bitdeer",
            "AZURE_OPENAI_ENDPOINT=https://example-resource.openai.azure.com/",
            "AZURE_OPENAI_API_KEY=" + FakeKey,
        },
        "BITDEER_API_KEY")]
    [InlineData(
        new[]
        {
            "AI_INFERENCE_PROVIDER=foundry",
            "FOUNDRY_ENDPOINT=https://example-foundry.openai.azure.com/",
            "AZURE_OPENAI_ENDPOINT=https://example-resource.openai.azure.com/",
            "AZURE_OPENAI_API_KEY=" + FakeKey,
        },
        "FOUNDRY_API_KEY")]
    public void DeploymentOption_NeverStandsInForACredentialOrAnotherProvidersSelection(
        string[] assignments,
        string missing)
    {
        foreach (var option in new[] { "--deployment", "--model" })
        {
            var selection = ModelSelection.Resolve([option, "gpt-5.5"], forceOffline: false, Env(Parse(assignments)));

            Assert.False(selection.IsLive);
            Assert.Equal(InferenceProvider.None, selection.Settings.Provider);
            Assert.Null(selection.Model);
            Assert.Contains(missing, selection.Settings.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeKey, selection.Describe(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelOption_DoesNotChangeWhichProviderAWorkingEnvironmentPicks()
    {
        // Bitdeer resolves on its own; Azure lacks only its deployment. --model names Bitdeer's model, and must
        // not complete Azure and so flip auto-detection (Azure is earlier in the order) to a host nobody chose.
        var selection = ModelSelection.Resolve(
            ["--model", "zai-org/GLM-5.3-Flash"],
            forceOffline: false,
            Env(
                ("BITDEER_API_KEY", FakeKey),
                ("AZURE_OPENAI_ENDPOINT", "https://example-resource.openai.azure.com/"),
                ("AZURE_OPENAI_API_KEY", FakeKey)));

        Assert.True(selection.IsLive);
        Assert.Equal(InferenceProvider.Bitdeer, selection.Settings.Provider);
        Assert.Equal("zai-org/GLM-5.3-Flash", selection.Model);
    }

    [Fact]
    public void ModelOverride_NeverSelectsOrCredentialsAProvider()
    {
        var selection = ModelSelection.Resolve(["--model", "gpt-5.5"], forceOffline: false, Env());

        Assert.False(selection.IsLive);
        Assert.Null(selection.Model);
        // The flag is not an Azure variable: an empty environment is still "nothing configured", not a half-done
        // Azure setup missing its endpoint and key.
        Assert.DoesNotContain("partially configured", selection.Settings.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("For the live path", selection.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The SDK that built the client: the assembly defining its inner <see cref="ChatClient"/>. The Azure SDK's
    /// subclass of it is internal, so the assembly is the observable difference.
    /// </summary>
    private static Assembly SdkOf(IChatClient client)
    {
        var inner = client.GetService<ChatClient>();
        Assert.NotNull(inner);
        return inner.GetType().Assembly;
    }

    private static (string Name, string Value)[] Parse(IEnumerable<string> assignments) =>
        [.. assignments.Select(a => (a[..a.IndexOf('=', StringComparison.Ordinal)], a[(a.IndexOf('=', StringComparison.Ordinal) + 1)..]))];

    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }
}
