// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Phase-6 Task 6.8 — pins the contract that the GDPR / EU AI Act benchmark
/// assemblies ship judge-system prompts as embedded resources AND that
/// <see cref="EmbeddedPromptLoader"/> can locate them by suffix. Without this
/// pin, a future build-action regression (e.g. someone changing the YAML file's
/// <c>&lt;EmbeddedResource&gt;</c> include to <c>&lt;None&gt;</c>) would silently
/// disable the prompts again — exactly the bug Phase-6 6.8 is fixing.
/// </summary>
public class EmbeddedPromptLoaderTests
{
    [Fact]
    public void Load_GdprJudgeSystemPrompt_ContainsCiteArticlesMarker()
    {
        var prompt = EmbeddedPromptLoader.Load(
            typeof(AgentEval.GdprBenchmark.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(prompt),
            "EmbeddedPromptLoader returned empty string for the GDPR judge prompt.");
        Assert.Contains("Cite articles", prompt);
        Assert.Contains("Be conservative", prompt);
        Assert.Contains("Flag evasive", prompt);
    }

    [Fact]
    public void Load_EuAiActJudgeSystemPrompt_ContainsCiteArticlesMarker()
    {
        var prompt = EmbeddedPromptLoader.Load(
            typeof(AgentEval.EuAiActBenchmark.EuAiActBenchmark).Assembly,
            "eu-ai-act-judge-system.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(prompt),
            "EmbeddedPromptLoader returned empty string for the EU AI Act judge prompt.");
        Assert.Contains("Cite articles", prompt);
        Assert.Contains("Be conservative", prompt);
        Assert.Contains("Flag evasive", prompt);
    }

    [Fact]
    public void Load_MissingResource_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EmbeddedPromptLoader.Load(
                typeof(AgentEval.GdprBenchmark.GdprBenchmark).Assembly,
                "definitely-not-a-real-file.v9.md"));
    }

    [Fact]
    public void Load_SameSuffixTwice_ReturnsCachedResult()
    {
        var first = EmbeddedPromptLoader.Load(
            typeof(AgentEval.GdprBenchmark.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");
        var second = EmbeddedPromptLoader.Load(
            typeof(AgentEval.GdprBenchmark.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");

        // Reference-equal proves the cache served the second call.
        Assert.Same(first, second);
    }
}
