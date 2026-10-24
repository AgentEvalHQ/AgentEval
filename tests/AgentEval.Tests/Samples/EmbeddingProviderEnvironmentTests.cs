// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Providers;
using AgentEval.Samples;
using Xunit;

namespace AgentEval.Tests.Samples;

/// <summary>
/// The samples' embeddings follow the chat provider <c>AI_INFERENCE_PROVIDER</c> selects. Until 2026-10 they were
/// Azure-only, so sample B1 skipped on every other provider. These drive the sample file itself and pin the contract:
/// the same host and key as chat, a default model only on Bitdeer and OpenAI, a named model everywhere else, and never
/// a host the user did not choose. Every variable source is a dictionary: no test reads the process environment.
/// </summary>
public class EmbeddingProviderEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static EmbeddingProviderSettings Resolve(params (string Name, string Value)[] vars)
    {
        var env = Env(vars);
        return EmbeddingProviderEnvironment.Resolve(InferenceProviderEnvironment.Resolve(env), env);
    }

    private static readonly (string, string)[] AzureVars = [("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "ak"), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o")];
    private static readonly (string, string)[] BitdeerVars = [("BITDEER_API_KEY", "bk")];
    private static readonly (string, string)[] OpenAIVars = [("OPENAI_API_KEY", "ok")];
    private static readonly (string, string)[] FoundryVars = [("FOUNDRY_ENDPOINT", "https://f.openai.azure.com/"), ("FOUNDRY_API_KEY", "fk"), ("FOUNDRY_MODEL", "gpt-4.1")];
    private static readonly (string, string)[] CompatVars = [("OPENAI_COMPATIBLE_ENDPOINT", "http://localhost:11434/v1"), ("OPENAI_COMPATIBLE_MODEL", "llama3.1")];

    // ── Defaults where the provider's catalogue makes one obvious ────────────

    [Fact]
    public void Bitdeer_DefaultsToBgeM3_OnTheChatEndpoint_WithTheChatKey()
    {
        var e = Resolve([.. BitdeerVars, ("AI_INFERENCE_PROVIDER", "bitdeer")]);

        Assert.True(e.IsConfigured);
        Assert.Equal(InferenceProvider.Bitdeer, e.Provider);
        Assert.Equal("BAAI/bge-m3", e.Model);
        Assert.Equal(EmbeddingProviderEnvironment.BitdeerDefaultModel, e.Model);
        Assert.True(e.ModelIsDefault);
        Assert.Equal("BITDEER_EMBEDDING_MODEL", e.ModelVariable);
        Assert.Equal(new Uri(InferenceProviderEnvironment.BitdeerDefaultEndpoint), e.Endpoint);
        Assert.Equal("bk", e.ApiKey);
        Assert.False(e.UsesAzureProtocol);
        Assert.Equal("BAAI/bge-m3@bitdeer", e.ModelIdentity);
        Assert.Null(e.Diagnostic);
    }

    [Fact]
    public void Bitdeer_NamedModel_And_Endpoint_AreHonoured()
    {
        var e = Resolve([.. BitdeerVars, ("BITDEER_ENDPOINT", "https://proxy.example/v1"), ("BITDEER_EMBEDDING_MODEL", "  some/other-embedder  ")]);

        Assert.Equal("some/other-embedder", e.Model);
        Assert.False(e.ModelIsDefault);
        Assert.Equal(new Uri("https://proxy.example/v1"), e.Endpoint);
    }

    [Fact]
    public void OnANonDefaultEndpoint_TheProvidersUsualModelIsNotAssumed()
    {
        // A proxy or gateway's catalogue is unknown: taking bge-m3 or text-embedding-3-small there would be a guess.
        var bitdeerProxy = Resolve([.. BitdeerVars, ("BITDEER_ENDPOINT", "https://proxy.example/v1")]);
        var openAIGateway = Resolve([.. OpenAIVars, ("AI_INFERENCE_PROVIDER", "openai"), ("OPENAI_BASE_URL", "https://gateway.example/v1")]);
        var bitdeerDefaultWithSlash = Resolve([.. BitdeerVars, ("BITDEER_ENDPOINT", InferenceProviderEnvironment.BitdeerDefaultEndpoint + "/")]);

        Assert.False(bitdeerProxy.IsConfigured);
        Assert.Contains("BITDEER_EMBEDDING_MODEL", bitdeerProxy.Diagnostic);
        Assert.Contains("not its default one", bitdeerProxy.Diagnostic);
        Assert.False(openAIGateway.IsConfigured);
        Assert.Contains("OPENAI_EMBEDDING_MODEL", openAIGateway.Diagnostic);
        Assert.Equal(EmbeddingProviderEnvironment.BitdeerDefaultModel, bitdeerDefaultWithSlash.Model);
    }

    [Fact]
    public void OpenAI_DefaultsToTextEmbedding3Small_AndFollowsOpenAIBaseUrl()
    {
        var byDefault = Resolve([.. OpenAIVars, ("AI_INFERENCE_PROVIDER", "openai")]);
        var named = Resolve([.. OpenAIVars, ("OPENAI_BASE_URL", "https://gateway.example/v1"), ("OPENAI_EMBEDDING_MODEL", "text-embedding-3-large")]);

        Assert.Equal("text-embedding-3-small", byDefault.Model);
        Assert.True(byDefault.ModelIsDefault);
        Assert.Equal(new Uri(InferenceProviderEnvironment.OpenAIDefaultEndpoint), byDefault.Endpoint);
        Assert.Equal("text-embedding-3-large", named.Model);
        Assert.Equal(new Uri("https://gateway.example/v1"), named.Endpoint);
        Assert.Equal("ok", named.ApiKey);
    }

    // ── A named model where there is no obvious default ──────────────────────

    [Theory]
    [InlineData("azure", "AZURE_OPENAI_EMBEDDING_DEPLOYMENT")]
    [InlineData("foundry", "FOUNDRY_EMBEDDING_MODEL")]
    [InlineData("openai-compatible", "OPENAI_COMPATIBLE_EMBEDDING_MODEL")]
    public void ProvidersWithoutADefault_AreNotConfigured_UntilTheModelIsNamed_AndSayWhichVariable(string selector, string variable)
    {
        (string, string)[] chat = [.. AzureVars, .. FoundryVars, .. CompatVars, ("AI_INFERENCE_PROVIDER", selector)];

        var missing = Resolve(chat);
        var whitespace = Resolve([.. chat, (variable, "   ")]);
        var named = Resolve([.. chat, (variable, "my-embedder")]);

        Assert.False(missing.IsConfigured);
        Assert.Null(missing.Model);
        Assert.Equal(variable, missing.ModelVariable);
        Assert.Contains(variable, missing.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("no default embedding model", missing.Diagnostic, StringComparison.Ordinal);
        // Nothing can build a client from a not-configured result.
        Assert.Null(missing.Endpoint);
        Assert.Null(missing.ApiKey);
        Assert.False(whitespace.IsConfigured);

        Assert.True(named.IsConfigured);
        Assert.Equal("my-embedder", named.Model);
        Assert.False(named.ModelIsDefault);
        Assert.Null(named.Diagnostic);
    }

    [Fact]
    public void Azure_KeepsTheSameVariables_AsBefore_TheTrioPlusTheEmbeddingDeployment()
    {
        var e = Resolve([.. AzureVars, ("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small")]);

        Assert.Equal(InferenceProvider.AzureOpenAI, e.Provider);
        Assert.True(e.UsesAzureProtocol);
        Assert.Equal("text-embedding-3-small", e.Model);
        Assert.Equal(new Uri("https://r.openai.azure.com/"), e.Endpoint);
        Assert.Equal("ak", e.ApiKey);
    }

    [Fact]
    public void Foundry_SpeaksTheAzureProtocol_OnTheFoundryEndpoint()
    {
        var e = Resolve([.. FoundryVars, ("FOUNDRY_EMBEDDING_MODEL", "text-embedding-3-small")]);

        Assert.Equal(InferenceProvider.Foundry, e.Provider);
        Assert.True(e.UsesAzureProtocol);
        Assert.Equal(new Uri("https://f.openai.azure.com/"), e.Endpoint);
        Assert.Equal("fk", e.ApiKey);
    }

    [Fact]
    public void OpenAICompatible_KeylessHost_GetsTheSameSentinelAsChat()
    {
        var e = Resolve([.. CompatVars, ("OPENAI_COMPATIBLE_EMBEDDING_MODEL", "bge-m3")]);

        Assert.True(e.IsConfigured);
        Assert.Equal(InferenceProviderEnvironment.NoKeyNeeded, e.ApiKey);
        Assert.Equal(new Uri("http://localhost:11434/v1"), e.Endpoint);
    }

    // ── Never a host the user did not choose ─────────────────────────────────

    [Fact]
    public void Embeddings_FollowTheSelector_EvenWhenAzureEmbeddingsAreFullyConfigured()
    {
        // Before 2026-10 this configuration sent B1's embeddings to Azure while Bitdeer served the chat.
        var e = Resolve([.. AzureVars, .. BitdeerVars, ("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-ada-002"), ("AI_INFERENCE_PROVIDER", "bitdeer")]);

        Assert.Equal(InferenceProvider.Bitdeer, e.Provider);
        Assert.Equal("BAAI/bge-m3", e.Model);
        Assert.Equal(new Uri(InferenceProviderEnvironment.BitdeerDefaultEndpoint), e.Endpoint);
        Assert.Equal("bk", e.ApiKey);
    }

    [Fact]
    public void AnotherProvidersEmbeddingVariable_IsIgnored()
    {
        var e = Resolve([.. BitdeerVars, .. OpenAIVars, ("OPENAI_EMBEDDING_MODEL", "text-embedding-3-large"), ("AI_INFERENCE_PROVIDER", "bitdeer")]);

        Assert.Equal("BAAI/bge-m3", e.Model);
        Assert.True(e.ModelIsDefault);
    }

    [Fact]
    public void AutoDetectedChatProvider_IsTheOneEmbeddingsFollow()
    {
        // No selector: Azure wins chat auto-detection, so embeddings need Azure's deployment variable, not Bitdeer's default.
        var e = Resolve([.. AzureVars, .. BitdeerVars]);

        Assert.Equal(InferenceProvider.AzureOpenAI, e.Provider);
        Assert.False(e.IsConfigured);
        Assert.Contains("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", e.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void NoChatProvider_MeansNoEmbeddings_AndCarriesTheChatDiagnostic()
    {
        var nothing = Resolve();
        var explicitButMissing = Resolve([.. AzureVars, ("AI_INFERENCE_PROVIDER", "bitdeer")]);

        Assert.False(nothing.IsConfigured);
        Assert.Equal(InferenceProvider.None, nothing.Provider);
        Assert.Contains("Embeddings follow the chat provider", nothing.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("BITDEER_API_KEY", nothing.Diagnostic, StringComparison.Ordinal);

        // The chat selector failed closed (bitdeer has no key); embeddings must not fall back to the Azure trio.
        Assert.False(explicitButMissing.IsConfigured);
        Assert.Null(explicitButMissing.Endpoint);
        Assert.Contains("AI_INFERENCE_PROVIDER=bitdeer", explicitButMissing.Diagnostic, StringComparison.Ordinal);
    }

    // ── Hygiene ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_NeverPrintsTheKey()
    {
        var e = Resolve([("BITDEER_API_KEY", "sk-very-secret-value")]);

        Assert.DoesNotContain("sk-very-secret-value", e.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_CredentialBearingEndpoint_DoesNotExposeItsSecrets()
    {
        var settings = Resolve(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", "https://url-user:url-password@example.test/path-secret?token=query-secret#fragment-secret"),
            ("OPENAI_COMPATIBLE_API_KEY", "private-key"),
            ("OPENAI_COMPATIBLE_MODEL", "subject"),
            ("OPENAI_COMPATIBLE_EMBEDDING_MODEL", "embedding"));
        Assert.True(settings.IsConfigured);

        var text = settings.ToString();

        foreach (var secret in new[] { "url-user", "url-password", "path-secret", "query-secret", "fragment-secret", "private-key" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProvider_HasAnEmbeddingVariable_AndTheListNamesThemAll()
    {
        var providers = Enum.GetValues<InferenceProvider>().Where(p => p != InferenceProvider.None).ToList();

        Assert.All(providers, p => Assert.NotNull(EmbeddingProviderEnvironment.ModelVariableOf(p)));
        Assert.Equal(
            providers.Select(p => EmbeddingProviderEnvironment.ModelVariableOf(p)!).OrderBy(v => v, StringComparer.Ordinal),
            EmbeddingProviderEnvironment.AllEmbeddingVariables.OrderBy(v => v, StringComparer.Ordinal));
        // None of them configures a chat provider: they must not make a machine look "configuration attempted".
        Assert.All(EmbeddingProviderEnvironment.AllEmbeddingVariables,
            v => Assert.DoesNotContain(v, InferenceProviderEnvironment.AllProviderVariables));
    }
}
