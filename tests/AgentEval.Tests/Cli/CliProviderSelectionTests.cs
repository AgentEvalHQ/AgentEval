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
    // ── The banner must not echo a credential ───────────────────────────────────────────

    [Theory]
    [InlineData("https://user:sekret@example.openai.azure.com/v1", "https://example.openai.azure.com/v1")]
    [InlineData("https://host.example/v1?api-key=sekret", "https://host.example/v1")]
    [InlineData("https://host.example/v1#sekret", "https://host.example/v1")]
    [InlineData("https://host.example:8443/v1/", "https://host.example:8443/v1")]
    [InlineData("https://host.example/", "https://host.example")]
    public void SafeEndpoint_DropsEveryPartOfAUrlThatCouldCarryAToken(string configured, string expected)
    {
        // The banner goes to stderr on every run, and a CI log keeps it. An endpoint is user-configured and
        // may legitimately carry user-info, a query or a fragment — any of which can hold a key.
        var printed = ProviderChatClientFactory.SafeEndpoint(new Uri(configured));

        Assert.Equal(expected, printed);
        Assert.DoesNotContain("sekret", printed, StringComparison.Ordinal);
    }

    // ── The stub rescues an unconfigured machine, never a misconfigured one ──────────────

    [Fact]
    public void Judge_ExplicitSelectorWithMissingVariables_DoesNotFallThroughToTheStub_EvenWhenItIsAllowed()
    {
        // The hole this pins: AI_INFERENCE_PROVIDER=foundry with its variables missing, plus the stub
        // opt-in, used to return a StubEvaluator — turning a typo into stub-graded evidence and undoing
        // the resolver's fail-closed contract from underneath.
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "foundry"),
            ("AGENTEVAL_ALLOW_STUB_JUDGE", "1"));
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var (judge, _, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

            Assert.Null(judge);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("misconfigured", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void Judge_NothingConfiguredAtAll_StillAllowsTheStubWhenItIsExplicitlyEnabled()
    {
        // The other side of the same gate: a machine with no provider at all is the stub's legitimate use.
        using var _ = new ProviderEnvironmentScope(("AGENTEVAL_ALLOW_STUB_JUDGE", "1"));

        var (judge, judgeModel, exitCode) = JudgeFactory.Resolve(evaluatorOverride: null);

        Assert.NotNull(judge);
        Assert.Equal(0, exitCode);
        Assert.Equal("stub", judgeModel);
    }

    [Fact]
    public void AnyConfigurationAttempted_SeparatesAnUnconfiguredMachineFromAMisconfiguredOne()
    {
        using (var __ = new ProviderEnvironmentScope())
            Assert.False(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));

        using (var __ = new ProviderEnvironmentScope(("AI_INFERENCE_PROVIDER", "foundry")))
            Assert.True(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));

        // One of Azure's three is enough to count as an attempt.
        using var ___ = new ProviderEnvironmentScope(("AZURE_OPENAI_ENDPOINT", AzureEndpoint));
        Assert.True(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));
    }

    [Theory]
    [InlineData("OPENAI_BASE_URL", "https://proxy.example/v1")]
    [InlineData("OPENAI_MODEL", "gpt-4o-mini")]
    [InlineData("BITDEER_MODEL", "zai-org/GLM-5.3-Flash")]
    [InlineData("OPENAI_COMPATIBLE_ENDPOINT", "http://127.0.0.1:11434/v1")]
    public void AnOptionalVariableAloneStillCountsAsAConfigurationAttempt(string name, string value)
    {
        // Someone who set only a base URL or a model has tried to configure a provider and made a mistake.
        // Counting required credentials alone would let the stub rescue exactly that case.
        using var _ = new ProviderEnvironmentScope((name, value));

        Assert.True(InferenceProviderEnvironment.AnyConfigurationAttempted(Environment.GetEnvironmentVariable));
    }

    [Fact]
    public void AnInvalidEndpoint_IsDiagnosedByVariableName_NeverByQuotingTheUrl()
    {
        // The diagnostic reaches stderr, and a CI log keeps it. An endpoint is user-configured and can carry
        // a token in its user-info, query or fragment.
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai-compatible"),
            ("OPENAI_COMPATIBLE_ENDPOINT", "ftp://user:sekret@example.test/v1"),
            ("OPENAI_COMPATIBLE_MODEL", "some-model"));

        var settings = ProviderChatClientFactory.Settings;

        Assert.False(settings.IsConfigured);
        Assert.Contains("OPENAI_COMPATIBLE_ENDPOINT", settings.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("sekret", settings.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("ftp://", settings.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestScrubList_CoversEveryVariableTheResolverReads()
    {
        // A variable added to the resolver but not to the scrub list would let an ambient value leak into
        // every test in this collection — silently, and only on the machine that has it set.
        var missing = InferenceProviderEnvironment.AllProviderVariables
            .Where(v => !ProviderEnvironmentScope.Variables.Contains(v, StringComparer.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"not scrubbed by ProviderEnvironmentScope: {string.Join(", ", missing)}");
    }

}
