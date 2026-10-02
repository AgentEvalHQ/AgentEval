// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for <see cref="CalibrationJudgeIdentity"/>: the judge provider and model every <c>bench … calibrate</c>
/// report names in its header.
/// </summary>
/// <remarks>
/// The identity is read back from the variables <see cref="JudgeFactory"/> resolved, so most of these tests build
/// the judge with the real <see cref="JudgeFactory.Resolve"/> first and identify what it returned. A change to the
/// factory's precedence that the identity does not follow then fails here instead of naming the wrong provider in
/// a report. Building a judge makes no network call.
/// </remarks>
[Collection("EnvVarTests")]
public class CalibrationJudgeIdentityTests
{
    private const string AzureKey = "test-secret-azure-key";
    private const string JudgeKey = "test-secret-judge-key";
    private const string OpenAIKey = "test-secret-openai-key";

    private sealed class FakeEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 100, Summary = "fake" });
    }

    private static CalibrationJudgeIdentity ResolveAndIdentify()
    {
        var (judge, model, exit) = JudgeFactory.Resolve(null, "test calibration");
        Assert.Equal(0, exit);
        Assert.NotNull(judge);
        return CalibrationJudgeIdentity.Of(null, null, judge!, model);
    }

    private static void AssertCarriesNoSecretOrEndpoint(CalibrationJudgeIdentity identity, params string[] forbidden)
    {
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, identity.Provider, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, identity.Model, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Of_AzureOpenAIAutoDetected_NamesProviderAndDeployment()
    {
        using var env = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", "https://contoso-calib.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", AzureKey),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini"));

        var identity = ResolveAndIdentify();

        Assert.Equal("Azure OpenAI (azure, auto-detected)", identity.Provider);
        Assert.Equal("gpt-4o-mini", identity.Model);
        AssertCarriesNoSecretOrEndpoint(identity, AzureKey, "contoso-calib", "https://");
    }

    [Fact]
    public void Of_ExplicitSelector_NamesTheSelectedProviderNotTheOtherConfiguredOne()
    {
        // Azure is configured too; the selector picks OpenAI, and so does the judge.
        using var env = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "openai"),
            ("OPENAI_API_KEY", OpenAIKey),
            ("OPENAI_MODEL", "gpt-4.1"),
            ("AZURE_OPENAI_ENDPOINT", "https://contoso-calib.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", AzureKey),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini"));

        var identity = ResolveAndIdentify();

        Assert.Equal("OpenAI (AI_INFERENCE_PROVIDER=openai)", identity.Provider);
        Assert.Equal("gpt-4.1", identity.Model);
        AssertCarriesNoSecretOrEndpoint(identity, OpenAIKey, AzureKey, "api.openai.com", "https://");
    }

    [Fact]
    public void Of_JudgeSpecificAzureTrio_WinsOverTheGeneralProvider()
    {
        using var env = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_JUDGE_ENDPOINT", "https://judge-calib.openai.azure.com/"),
            ("AZURE_OPENAI_JUDGE_API_KEY", JudgeKey),
            ("AZURE_OPENAI_JUDGE_DEPLOYMENT", "judge-deployment"),
            ("OPENAI_API_KEY", OpenAIKey));

        var identity = ResolveAndIdentify();

        Assert.Equal("Azure OpenAI (judge-specific endpoint, AZURE_OPENAI_JUDGE_*)", identity.Provider);
        Assert.Equal("judge-deployment", identity.Model);
        AssertCarriesNoSecretOrEndpoint(identity, JudgeKey, OpenAIKey, "judge-calib", "https://");
    }

    [Fact]
    public void Of_StubJudge_SaysItIsTheStubAndNamesNoModel()
    {
        using var env = new ProviderEnvironmentScope(("AGENTEVAL_ALLOW_STUB_JUDGE", "1"));

        var identity = ResolveAndIdentify();

        Assert.StartsWith("stub (AGENTEVAL_ALLOW_STUB_JUDGE=1)", identity.Provider);
        Assert.Equal("none", identity.Model);
    }

    [Fact]
    public void Of_EnvironmentChangedAfterTheJudgeWasBuilt_ReportsTheProviderAsUnknown()
    {
        using var env = new ProviderEnvironmentScope(
            ("AZURE_OPENAI_ENDPOINT", "https://contoso-calib.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", AzureKey),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini"));
        var (judge, model, _) = JudgeFactory.Resolve(null, "test calibration");

        // The variables now name another deployment: naming Azure for "gpt-4o-mini" would be a guess.
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1");
        var identity = CalibrationJudgeIdentity.Of(null, null, judge!, model);

        Assert.StartsWith("unknown", identity.Provider);
        Assert.Equal("gpt-4o-mini", identity.Model);
    }

    [Fact]
    public void Of_SuppliedEvaluatorWithoutIdentity_ReportsBothAsUnknownAndNamesTheType()
    {
        var fake = new FakeEvaluator();

        var identity = CalibrationJudgeIdentity.Of(fake, null, fake, "override", _ => null);

        Assert.StartsWith("unknown", identity.Provider);
        Assert.Contains(nameof(FakeEvaluator), identity.Provider);
        Assert.StartsWith("unknown", identity.Model);
    }

    [Fact]
    public void Of_SuppliedEvaluatorWithIdentity_UsesTheCallersIdentity()
    {
        var fake = new FakeEvaluator();

        var identity = CalibrationJudgeIdentity.Of(
            fake, new CalibrationJudgeIdentity("Decision provider", "decision-model-1"), fake, "override", _ => null);

        Assert.Equal("Decision provider", identity.Provider);
        Assert.Equal("decision-model-1", identity.Model);
    }

    [Fact]
    public void Of_IdentityWithoutASuppliedEvaluator_IsIgnored()
    {
        // The environment chose this judge, so a caller-supplied name for it is not trusted.
        var env = new Dictionary<string, string?>
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://contoso-calib.openai.azure.com/",
            ["AZURE_OPENAI_API_KEY"] = AzureKey,
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-4o-mini",
        };

        var identity = CalibrationJudgeIdentity.Of(
            null, new CalibrationJudgeIdentity("Somebody else", "another-model"), new FakeEvaluator(), "gpt-4o-mini",
            name => env.TryGetValue(name, out var value) ? value : null);

        Assert.Equal("Azure OpenAI (azure, auto-detected)", identity.Provider);
        Assert.Equal("gpt-4o-mini", identity.Model);
    }

    [Fact]
    public void Of_ControlCharactersInAName_StayOnOneHeaderLine()
    {
        var fake = new FakeEvaluator();

        var identity = CalibrationJudgeIdentity.Of(
            fake, new CalibrationJudgeIdentity("Provider\nInjected: line", "model\r\nx"), fake, "override", _ => null);

        Assert.Equal("Provider Injected: line", identity.Provider);
        Assert.Equal("model  x", identity.Model);
    }

    [Fact]
    public void AppendMarkdownHeader_WritesTheProviderAndModelLines()
    {
        var sb = new StringBuilder();

        new CalibrationJudgeIdentity("Azure OpenAI (azure, auto-detected)", "gpt-4o-mini").AppendMarkdownHeader(sb);

        var lines = sb.ToString().Split(Environment.NewLine);
        Assert.Contains("Judge provider: Azure OpenAI (azure, auto-detected)", lines);
        Assert.Contains("Judge model: gpt-4o-mini", lines);
    }
}
