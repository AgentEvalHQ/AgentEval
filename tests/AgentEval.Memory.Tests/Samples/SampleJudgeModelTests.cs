// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.Models;
using AgentEval.Providers;
using AgentEval.Samples;
using Xunit;

namespace AgentEval.Memory.Tests.Samples;

public class SampleJudgeModelTests
{
    [Theory]
    [InlineData("azure", "AZURE_OPENAI_DEPLOYMENT")]
    [InlineData("bitdeer", "BITDEER_MODEL")]
    [InlineData("openai", "OPENAI_MODEL")]
    [InlineData("foundry", "FOUNDRY_MODEL")]
    [InlineData("openai-compatible", "OPENAI_COMPATIBLE_MODEL")]
    public void Resolve_ConfiguredSecondary_UsesItWithoutChangingPrimary(string provider, string prefix)
    {
        var env = EnvironmentFor(provider);
        env[prefix + "_2"] = "judge";
        var settings = InferenceProviderEnvironment.Resolve(env.GetValueOrDefault);

        Assert.True(settings.IsConfigured);
        Assert.Equal("judge", SampleJudgeModel.Resolve(settings, env.GetValueOrDefault));
        Assert.Equal("subject", settings.Model);
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("bitdeer")]
    public void Resolve_NoNamedAlternative_UsesPrimaryRatherThanInventedDefaults(string provider)
    {
        var env = EnvironmentFor(provider);
        var settings = InferenceProviderEnvironment.Resolve(env.GetValueOrDefault);

        var judge = SampleJudgeModel.Resolve(settings, env.GetValueOrDefault);

        Assert.Equal("subject", judge);
        Assert.Contains("self-judged", SampleJudgeModel.DescribeFor(settings.Model!, judge));
    }

    [Fact]
    public void Resolve_SecondaryMatchesPrimary_UsesDistinctNamedThird()
    {
        var env = EnvironmentFor("bitdeer");
        env["BITDEER_MODEL_2"] = "SUBJECT";
        env["BITDEER_MODEL_3"] = "other-judge";

        Assert.Equal("other-judge", SampleJudgeModel.Resolve(
            InferenceProviderEnvironment.Resolve(env.GetValueOrDefault), env.GetValueOrDefault));
    }

    [Fact]
    public void Resolve_OnlyThirdIsNamed_UsesThatAvailableAlternative()
    {
        var env = EnvironmentFor("azure");
        env["AZURE_OPENAI_DEPLOYMENT_3"] = "third-judge";

        Assert.Equal("third-judge", SampleJudgeModel.Resolve(
            InferenceProviderEnvironment.Resolve(env.GetValueOrDefault), env.GetValueOrDefault));
    }

    [Fact]
    public void Resolve_NoProvider_RefusesAnUnknownJudge()
    {
        static string? Empty(string _) => null;
        var settings = InferenceProviderEnvironment.Resolve(Empty);

        Assert.Throws<InvalidOperationException>(() => SampleJudgeModel.Resolve(settings, Empty));
    }

    [Theory]
    [InlineData("subject", "subject", true)]
    [InlineData("SUBJECT", "subject", true)]
    [InlineData("subject", "judge", false)]
    public void DescribeFor_MatchingIdentifiers_DisclosesSelfJudging(string subject, string judge, bool selfJudged)
    {
        var description = SampleJudgeModel.DescribeFor(subject, judge);

        Assert.Contains(judge, description);
        Assert.Equal(selfJudged, description.Contains("self-judged", StringComparison.Ordinal));
    }

    [Fact]
    public void BaselineConfiguration_JudgeChanges_SeparatesIdentityAndSurvivesSerialization()
    {
        static AgentBenchmarkConfig Config(string judge, string provider) => new()
        {
            AgentName = "MemoryAgent", ModelId = "subject",
            CustomConfig = SampleJudgeModel.BaselineConfiguration(judge, provider)
        };
        var first = Config("first-judge", "host");
        var changedJudge = Config("second-judge", "host");
        var changedProvider = Config("first-judge", "another-host");
        var roundtrip = JsonSerializer.Deserialize<AgentBenchmarkConfig>(JsonSerializer.Serialize(first))!;

        Assert.NotEqual(first.ConfigurationId, changedJudge.ConfigurationId);
        Assert.NotEqual(first.ConfigurationId, changedProvider.ConfigurationId);
        Assert.Equal(first.ConfigurationId, roundtrip.ConfigurationId);
        Assert.Equal("subject", roundtrip.ModelId);
        Assert.Equal("first-judge", roundtrip.CustomConfig["judgeModel"]);
    }

    private static Dictionary<string, string?> EnvironmentFor(string provider) => new()
    {
        ["AI_INFERENCE_PROVIDER"] = provider,
        ["AZURE_OPENAI_ENDPOINT"] = "https://sample.openai.azure.com/",
        ["AZURE_OPENAI_API_KEY"] = "synthetic-key",
        ["AZURE_OPENAI_DEPLOYMENT"] = "subject",
        ["BITDEER_API_KEY"] = "synthetic-key", ["BITDEER_MODEL"] = "subject",
        ["OPENAI_API_KEY"] = "synthetic-key", ["OPENAI_MODEL"] = "subject",
        ["FOUNDRY_ENDPOINT"] = "https://sample.services.ai.azure.com/",
        ["FOUNDRY_API_KEY"] = "synthetic-key", ["FOUNDRY_MODEL"] = "subject",
        ["OPENAI_COMPATIBLE_ENDPOINT"] = "http://127.0.0.1:12345/v1",
        ["OPENAI_COMPATIBLE_MODEL"] = "subject"
    };
}
