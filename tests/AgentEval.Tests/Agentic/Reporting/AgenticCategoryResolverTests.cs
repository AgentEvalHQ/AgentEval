// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Agentic.Reporting;
using Xunit;

namespace AgentEval.Tests.Agentic.Reporting;

/// <summary>
/// Tests for the shared <see cref="AgenticCategoryResolver"/> (ARC-11) — the single keyword heuristic now
/// used by both the agentic summary builder and the PDF renderer for evaluators with no Metric.Category.
/// </summary>
public class AgenticCategoryResolverTests
{
    [Theory]
    [InlineData("task_completion", "system-outcome")]
    [InlineData("intent_resolution", "system-outcome")]
    [InlineData("tool_selection", "agentic-process")]
    [InlineData("planning_quality", "agentic-process")]
    [InlineData("rag_groundedness", "rag")]
    [InlineData("retrieval_relevance", "rag")]
    [InlineData("safety_harm", "safety-security")]
    [InlineData("refusal_rate", "safety-security")]
    [InlineData("judge_agreement", "judge-quality")]
    [InlineData("calibration_accuracy", "judge-quality")]
    [InlineData("latency_p95", "operational")]
    [InlineData("cost_per_run", "operational")]
    public void InferCategoryFromKey_MapsKnownKeywords(string key, string expected)
    {
        Assert.Equal(expected, AgenticCategoryResolver.InferCategoryFromKey(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something_unrecognised")]
    public void InferCategoryFromKey_UnknownOrNull_DefaultsToOperational(string? key)
    {
        Assert.Equal("operational", AgenticCategoryResolver.InferCategoryFromKey(key));
    }
}
