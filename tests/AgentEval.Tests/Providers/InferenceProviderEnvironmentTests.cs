// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Providers;
using Xunit;

namespace AgentEval.Tests.Providers;

/// <summary>
/// <c>AI_INFERENCE_PROVIDER</c> decides which host serves the model when several are credentialed.
/// These pin the contract: explicit beats detected, an explicit mistake fails closed with a reason,
/// auto-detection has a fixed order, and each provider's defaults are what the docs say.
/// </summary>
public class InferenceProviderEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static readonly (string, string)[] AzureVars = [("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "ak"), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o")];
    private static readonly (string, string)[] BitdeerVars = [("BITDEER_API_KEY", "bk")];
    private static readonly (string, string)[] OpenAIVars = [("OPENAI_API_KEY", "ok")];
    private static readonly (string, string)[] FoundryVars = [("FOUNDRY_ENDPOINT", "https://f.openai.azure.com/"), ("FOUNDRY_API_KEY", "fk"), ("FOUNDRY_MODEL", "gpt-4.1")];
    private static readonly (string, string)[] CompatVars = [("OPENAI_COMPATIBLE_ENDPOINT", "http://localhost:11434/v1"), ("OPENAI_COMPATIBLE_API_KEY", "no-key-needed"), ("OPENAI_COMPATIBLE_MODEL", "llama3.1")];

    // ── Explicit selection ───────────────────────────────────────────────────

    [Fact]
    public void Explicit_Bitdeer_WinsOverAzure_WhenBothAreSet()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. AzureVars, .. BitdeerVars, ("AI_INFERENCE_PROVIDER", "bitdeer")]));

        Assert.Equal(InferenceProvider.Bitdeer, s.Provider);
        Assert.Equal(InferenceProviderSelection.Explicit, s.Selection);
        Assert.Equal("bitdeer", s.ProviderTag);
        Assert.Equal(new Uri(InferenceProviderEnvironment.BitdeerDefaultEndpoint), s.Endpoint);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, s.Model);
        Assert.Equal("bk", s.ApiKey);
        Assert.False(s.UsesAzureProtocol);
        Assert.Equal("zai-org/GLM-5.3-Flash@bitdeer", s.ModelIdentity);
    }

    [Theory]
    [InlineData("openai", InferenceProvider.OpenAI)]
    [InlineData("OpenAI", InferenceProvider.OpenAI)]
    [InlineData("foundry", InferenceProvider.Foundry)]
    [InlineData("azure", InferenceProvider.AzureOpenAI)]
    [InlineData("azure-openai", InferenceProvider.AzureOpenAI)]
    [InlineData("openai-compatible", InferenceProvider.OpenAICompatible)]
    [InlineData("compatible", InferenceProvider.OpenAICompatible)]
    public void Explicit_EachKnownValue_SelectsThatProvider(string value, InferenceProvider expected)
    {
        var all = new List<(string, string)>();
        all.AddRange(AzureVars); all.AddRange(BitdeerVars); all.AddRange(OpenAIVars); all.AddRange(FoundryVars); all.AddRange(CompatVars);
        all.Add(("AI_INFERENCE_PROVIDER", value));

        var s = InferenceProviderEnvironment.Resolve(Env([.. all]));

        Assert.Equal(expected, s.Provider);
        Assert.Equal(InferenceProviderSelection.Explicit, s.Selection);
        Assert.True(s.IsConfigured);
    }

    [Fact]
    public void Explicit_ProviderWithoutCredentials_FailsClosed_WithDiagnostic_NoSilentFallback()
    {
        // Azure IS configured, but the user asked for foundry and foundry has nothing. Falling back
        // to Azure would spend on a host the user did not choose.
        var s = InferenceProviderEnvironment.Resolve(Env([.. AzureVars, ("AI_INFERENCE_PROVIDER", "foundry")]));

        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.False(s.IsConfigured);
        Assert.Contains("AI_INFERENCE_PROVIDER=foundry", s.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_ENDPOINT", s.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_UnknownValue_FailsClosed_AndListsKnownValues()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. BitdeerVars, ("AI_INFERENCE_PROVIDER", "bitdeeer")]));

        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.Contains("'bitdeeer'", s.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("openai-compatible", s.Diagnostic, StringComparison.Ordinal);
    }

    // ── Auto-detection ───────────────────────────────────────────────────────

    [Fact]
    public void AutoDetect_Order_IsAzure_Bitdeer_OpenAI_Foundry_Compatible()
    {
        Assert.Equal(InferenceProvider.AzureOpenAI, InferenceProviderEnvironment.Resolve(Env([.. AzureVars, .. BitdeerVars, .. OpenAIVars])).Provider);
        Assert.Equal(InferenceProvider.Bitdeer, InferenceProviderEnvironment.Resolve(Env([.. BitdeerVars, .. OpenAIVars, .. FoundryVars])).Provider);
        Assert.Equal(InferenceProvider.OpenAI, InferenceProviderEnvironment.Resolve(Env([.. OpenAIVars, .. FoundryVars, .. CompatVars])).Provider);
        Assert.Equal(InferenceProvider.Foundry, InferenceProviderEnvironment.Resolve(Env([.. FoundryVars, .. CompatVars])).Provider);
        Assert.Equal(InferenceProvider.OpenAICompatible, InferenceProviderEnvironment.Resolve(Env([.. CompatVars])).Provider);
    }

    [Fact]
    public void AutoDetect_ReportsItself_AsAutoDetected()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. BitdeerVars]));
        Assert.Equal(InferenceProviderSelection.AutoDetected, s.Selection);
    }

    [Fact]
    public void Nothing_Set_IsNone_WithAHelpfulDiagnostic()
    {
        var s = InferenceProviderEnvironment.Resolve(Env());

        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.Equal(InferenceProviderSelection.None, s.Selection);
        Assert.Contains("BITDEER_API_KEY", s.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_ENDPOINT", s.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureTrio_Partial_DoesNotCount()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", "ak"), .. BitdeerVars]));
        Assert.Equal(InferenceProvider.Bitdeer, s.Provider);   // Azure without a deployment is not configured
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void OpenAI_Defaults_BaseUrlAndModel_AndOverrides()
    {
        var d = InferenceProviderEnvironment.Resolve(Env([.. OpenAIVars, ("AI_INFERENCE_PROVIDER", "openai")]));
        Assert.Equal(new Uri("https://api.openai.com/v1"), d.Endpoint);
        Assert.Equal("gpt-4o-mini", d.Model);
        Assert.Equal("gpt-4o-mini", d.SecondaryModel);

        var o = InferenceProviderEnvironment.Resolve(Env([.. OpenAIVars, ("OPENAI_BASE_URL", "https://proxy.example/v1"), ("OPENAI_MODEL", "gpt-5-mini"), ("OPENAI_MODEL_2", "gpt-4.1-mini"), ("AI_INFERENCE_PROVIDER", "openai")]));
        Assert.Equal(new Uri("https://proxy.example/v1"), o.Endpoint);
        Assert.Equal("gpt-5-mini", o.Model);
        Assert.Equal("gpt-4.1-mini", o.SecondaryModel);
        Assert.Equal("gpt-5-mini", o.TertiaryModel);
    }

    [Fact]
    public void Azure_SecondaryAndTertiary_KeepTheirHistoricalDefaults()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. AzureVars, ("AI_INFERENCE_PROVIDER", "azure")]));
        Assert.Equal("gpt-4o", s.Model);
        Assert.Equal("gpt-4o-mini", s.SecondaryModel);
        Assert.Equal("gpt-4.1", s.TertiaryModel);
        Assert.True(s.UsesAzureProtocol);
    }

    [Fact]
    public void Foundry_UsesAzureProtocol_AndItsOwnVariables()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. FoundryVars, ("AI_INFERENCE_PROVIDER", "foundry")]));
        Assert.True(s.UsesAzureProtocol);
        Assert.Equal(new Uri("https://f.openai.azure.com/"), s.Endpoint);
        Assert.Equal("fk", s.ApiKey);
        Assert.Equal("gpt-4.1@foundry", s.ModelIdentity);
    }

    [Fact]
    public void Bitdeer_SecondaryModel_DefaultsToPrimary_UnlessOverridden()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. BitdeerVars, ("BITDEER_MODEL_2", "zai-org/GLM-5.3-Air")]));
        Assert.Equal("zai-org/GLM-5.3-Flash", s.Model);
        Assert.Equal("zai-org/GLM-5.3-Air", s.SecondaryModel);
        Assert.Equal("zai-org/GLM-5.3-Flash", s.TertiaryModel);
    }

    [Fact]
    public void WhitespaceValues_CountAsUnset()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([("BITDEER_API_KEY", "   "), ("AI_INFERENCE_PROVIDER", "  ")]));
        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.Equal(InferenceProviderSelection.None, s.Selection);
    }

    [Fact]
    public void Parse_IsTolerant_AndNullForUnknown()
    {
        Assert.Equal(InferenceProvider.AzureOpenAI, InferenceProviderEnvironment.Parse(" Azure-OpenAI "));
        Assert.Equal(InferenceProvider.Foundry, InferenceProviderEnvironment.Parse("azure-ai-foundry"));
        Assert.Null(InferenceProviderEnvironment.Parse("anthropic"));
        Assert.Null(InferenceProviderEnvironment.Parse(null));
    }

    // ── Endpoints carry the key: https, or loopback http, or nothing ─────────

    [Fact]
    public void PlainHttpToARemoteHost_IsRefused_WithAReason_NotSentInCleartext()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. BitdeerVars, ("BITDEER_ENDPOINT", "http://proxy.example/v1"), ("AI_INFERENCE_PROVIDER", "bitdeer")]));

        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.Contains("BITDEER_ENDPOINT", s.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("cleartext", s.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopbackHttp_IsAllowed_ForLocalServers()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([.. CompatVars, ("AI_INFERENCE_PROVIDER", "openai-compatible")]));
        Assert.Equal(InferenceProvider.OpenAICompatible, s.Provider);
        Assert.Equal(new Uri("http://localhost:11434/v1"), s.Endpoint);
    }

    [Fact]
    public void MalformedEndpoint_IsRefused_WithAReason_InsteadOfThrowing()
    {
        var s = InferenceProviderEnvironment.Resolve(Env([("OPENAI_API_KEY", "ok"), ("OPENAI_BASE_URL", "not a url"), ("AI_INFERENCE_PROVIDER", "openai")]));

        Assert.Equal(InferenceProvider.None, s.Provider);
        Assert.Contains("OPENAI_BASE_URL", s.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("absolute http(s) URL", s.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ToString_NeverContainsTheKey()
    {
        const string key = "bk-DO-NOT-LEAK-7a2f";
        var s = InferenceProviderEnvironment.Resolve(Env([("BITDEER_API_KEY", key)]));

        var text = s.ToString();

        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
        Assert.Contains("bitdeer", text, StringComparison.Ordinal);
        Assert.Equal(key, s.ApiKey);   // the value itself is still available to the code that needs it
    }
}
