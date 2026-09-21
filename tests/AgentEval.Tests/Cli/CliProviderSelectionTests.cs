// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.Providers;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The CLI's env-var convention path resolves <c>AI_INFERENCE_PROVIDER</c> instead of assuming Azure OpenAI.
/// </summary>
/// <remarks>
/// Every <c>bench</c> and <c>calibrate</c> command reaches a model through <see cref="JudgeFactory"/> or
/// <see cref="AzureChatAgentFactory"/>, so these two funnels decide whether the CLI runs on a host other than
/// Azure at all. Constructing a client makes no network call, so all of this is offline.
/// </remarks>
[Collection("EnvVarTests")]   // every provider variable is cleared for this collection
public class CliProviderSelectionTests
{
    private const string BitdeerKey = "bd-test-key-not-real";
    private const string AzureEndpoint = "https://example.openai.azure.com/";

    // ── The selector reaches the judge ──────────────────────────────────────────────────

    [Fact]
    public void Judge_SelectorNamesBitdeer_ResolvesBitdeerAndReportsItsModel()
    {
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", BitdeerKey));

        var (judge, judgeModel, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

        Assert.NotNull(judge);
        Assert.Equal(0, exitCode);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, judgeModel);
    }

    [Fact]
    public void Agent_SelectorNamesBitdeer_ResolvesBitdeer()
    {
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", BitdeerKey));

        var (client, model, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();

        Assert.NotNull(client);
        Assert.Equal(0, exitCode);
        Assert.Equal(InferenceProviderEnvironment.BitdeerDefaultModel, model);
    }

    // ── Azure keeps working exactly as before ───────────────────────────────────────────

    [Fact]
    public void Judge_SelectorUnsetAndOnlyAzureConfigured_StillResolvesAzureWithItsDeployment()
    {
        // The back-compat case: an existing setup that never heard of the selector must be unaffected.
        using var _ = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", AzureEndpoint),
            ("AZURE_OPENAI_API_KEY", "az-test-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-back-compat"));

        var (judge, judgeModel, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

        Assert.NotNull(judge);
        Assert.Equal(0, exitCode);
        Assert.Equal("gpt-4o-back-compat", judgeModel);
    }

    [Fact]
    public void Judge_JudgeSpecificAzureVars_WinOverTheSelector()
    {
        // Pointing the grader at its own endpoint is the one case that still names a provider outright:
        // a capable judge against a cheap subject, in the same run.
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", BitdeerKey),
            ("AZURE_OPENAI_JUDGE_ENDPOINT", AzureEndpoint),
            ("AZURE_OPENAI_JUDGE_API_KEY", "az-judge-key"),
            ("AZURE_OPENAI_JUDGE_DEPLOYMENT", "gpt-4o-judge"));

        var (judge, judgeModel, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

        Assert.NotNull(judge);
        Assert.Equal(0, exitCode);
        Assert.Equal("gpt-4o-judge", judgeModel);   // the judge override, not the Bitdeer default
    }

    // ── Failing closed ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_ExplicitSelectorWithMissingVariables_FailsClosedAndNamesThem()
    {
        // Never silently grade on another host the operator did not choose — and never fall through to
        // the stub, which would produce stub-graded evidence from a typo.
        using var _ = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "foundry"));
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var (judge, _, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

            Assert.Null(judge);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("FOUNDRY_ENDPOINT", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void Judge_UnknownSelectorValue_FailsClosed()
    {
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "not-a-provider"),
            ("BITDEER_API_KEY", BitdeerKey));   // credentials exist, but not for what was asked for
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var (judge, _, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

            Assert.Null(judge);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("not-a-provider", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void PartiallyConfiguredProvider_NamesOnlyWhatIsMissing()
    {
        // The message that sent this test class into existence: a diagnostic must not claim a variable is
        // missing when it is set. Listing every provider's requirements did exactly that.
        using var _ = new ProviderEnvironmentScope(("AZURE_OPENAI_ENDPOINT", AzureEndpoint));

        var settings = ProviderChatClientFactory.Settings;

        Assert.False(settings.IsConfigured);
        Assert.Contains("AZURE_OPENAI_API_KEY", settings.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", settings.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE_OPENAI_ENDPOINT", settings.Diagnostic!, StringComparison.Ordinal);
    }

    // ── The settings are read, not remembered ───────────────────────────────────────────

    [Fact]
    public void Settings_AreReadFromTheEnvironmentEachTime_NotCachedFromTheFirstCall()
    {
        // A cached first resolution would make every later test — and any host that sets a variable at
        // startup — see a provider that is no longer selected. This is that regression, pinned.
        using (var __ = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", BitdeerKey)))
        {
            Assert.Equal(InferenceProvider.Bitdeer, ProviderChatClientFactory.Settings.Provider);
        }

        using var ___ = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", AzureEndpoint),
            ("AZURE_OPENAI_API_KEY", "az-test-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o"));

        Assert.Equal(InferenceProvider.AzureOpenAI, ProviderChatClientFactory.Settings.Provider);
    }
}
