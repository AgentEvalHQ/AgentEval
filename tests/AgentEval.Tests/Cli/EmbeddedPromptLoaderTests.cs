// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
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
            typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(prompt),
            "EmbeddedPromptLoader returned empty string for the GDPR judge prompt.");
        Assert.Contains("Cite articles", prompt);
        // Calibration-tuning replaced the "Be conservative" rule with a
        // "Grade on substance, not phrasing" rule to fix systematic
        // mark-correct-as-fail behaviour on multi-criterion rubrics.
        Assert.Contains("Grade on substance", prompt);
        Assert.Contains("Flag evasive", prompt);
    }

    [Fact]
    public void Load_EuAiActJudgeSystemPrompt_ContainsCiteArticlesMarker()
    {
        var prompt = EmbeddedPromptLoader.Load(
            typeof(AgentEval.Benchmarks.EuAiActBenchmark).Assembly,
            "eu-ai-act-judge-system.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(prompt),
            "EmbeddedPromptLoader returned empty string for the EU AI Act judge prompt.");
        Assert.Contains("Cite articles", prompt);
        // For the EU AI Act prompt, "Be conservative on Art 5" remains strict
        // for prohibited-practices articles, but the general-purpose rule
        // ("Be conservative in general") was replaced with "Grade on substance".
        Assert.Contains("Be conservative on Art 5", prompt);
        Assert.Contains("Grade on substance", prompt);
        Assert.Contains("Flag evasive", prompt);
    }

    [Fact]
    public void Load_MissingResource_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EmbeddedPromptLoader.Load(
                typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly,
                "definitely-not-a-real-file.v9.md"));
    }

    [Fact]
    public void Load_SameSuffixTwice_ReturnsCachedResult()
    {
        var first = EmbeddedPromptLoader.Load(
            typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");
        var second = EmbeddedPromptLoader.Load(
            typeof(AgentEval.Benchmarks.GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");

        // Reference-equal proves the cache served the second call.
        Assert.Same(first, second);
    }
}
