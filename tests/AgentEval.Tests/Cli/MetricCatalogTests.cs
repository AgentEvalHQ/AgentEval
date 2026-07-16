// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Item 4 (<c>strategy/CLI-Custom-Benchmarks-CopilotStudio-OpenAI-and-Metrics-Remediation-Design.md</c> §2 D,
/// candidate D1): <c>MetricCatalog</c> — the genuinely new bare-name -> <see cref="IMetric"/> resolver.
/// </summary>
public class MetricCatalogTests
{
    [Theory]
    [InlineData("code_tool_success")]
    [InlineData("code_tool_efficiency")]
    [InlineData("code_toxicity")]
    [InlineData("code_skill_disclosure_efficiency")]
    [InlineData("llm_relevance")]
    [InlineData("llm_faithfulness")]
    [InlineData("llm_context_precision")]
    [InlineData("llm_context_recall")]
    [InlineData("llm_answer_correctness")]
    [InlineData("llm_groundedness")]
    [InlineData("llm_coherence")]
    [InlineData("llm_fluency")]
    [InlineData("llm_bias")]
    [InlineData("llm_misinformation")]
    [InlineData("llm_task_completion")]
    public void IsKnown_EveryDocumentedName_ReturnsTrue(string name)
    {
        Assert.True(MetricCatalog.IsKnown(name));
    }

    [Theory]
    [InlineData("bogus_metric")]
    [InlineData("")]
    [InlineData("LLM_Relevance_Typo")]
    public void IsKnown_UnknownName_ReturnsFalse(string name)
    {
        Assert.False(MetricCatalog.IsKnown(name));
    }

    [Fact]
    public void IsKnown_IsCaseInsensitive()
    {
        Assert.True(MetricCatalog.IsKnown("LLM_RELEVANCE"));
        Assert.True(MetricCatalog.IsKnown("Code_Tool_Success"));
    }

    [Theory]
    [InlineData("code_tool_success")]
    [InlineData("code_tool_efficiency")]
    [InlineData("code_toxicity")]
    [InlineData("code_skill_disclosure_efficiency")]
    public void Resolve_CodeBasedMetric_NoClientNeeded_ReturnsInstanceWithMatchingName(string name)
    {
        var metric = MetricCatalog.Resolve(name, evaluatorClient: null);

        Assert.NotNull(metric);
        Assert.Equal(name, metric.Name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_LlmBasedMetric_WithClient_ReturnsInstance()
    {
        var client = new ScriptedChatClient();
        var metric = MetricCatalog.Resolve("llm_relevance", client);

        Assert.NotNull(metric);
        Assert.Equal("llm_relevance", metric.Name);
    }

    [Fact]
    public void Resolve_LlmBasedMetric_WithoutClient_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MetricCatalog.Resolve("llm_relevance", evaluatorClient: null));
        Assert.Contains("--judge", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownName_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => MetricCatalog.Resolve("bogus_metric", evaluatorClient: null));
        Assert.Contains("Unknown metric name", ex.Message);
        Assert.Contains("bogus_metric", ex.Message);
    }

    [Fact]
    public void AvailableNames_ContainsEveryCatalogedName_SortedAndDeduplicated()
    {
        var names = MetricCatalog.AvailableNames;

        Assert.Contains("llm_relevance", names);
        Assert.Contains("code_tool_success", names);
        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Count);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void Resolve_EachCallReturnsAFreshInstance()
    {
        // Resolve is a factory, not a singleton cache — each named metric a caller registers should be its
        // own instance (AgentEvalBuilder.AddMetric takes ownership of exactly one).
        var a = MetricCatalog.Resolve("code_tool_success", null);
        var b = MetricCatalog.Resolve("code_tool_success", null);
        Assert.NotSame(a, b);
    }
}
