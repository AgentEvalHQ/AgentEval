// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/MetricSelectionTests.cs
// during the v1.1 CLI consolidation.

using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the --metrics flag and EvaluationOptions.SelectedMetrics property.
/// </summary>
public class MetricSelectionTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // EVALUATION OPTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvaluationOptions_SelectedMetrics_DefaultsToNull()
    {
        var options = new EvaluationOptions();
        Assert.Null(options.SelectedMetrics);
    }

    [Fact]
    public void EvaluationOptions_SelectedMetrics_CanBeSet()
    {
        var options = new EvaluationOptions
        {
            SelectedMetrics = new[] { "llm_relevance", "code_tool_success" }
        };

        Assert.NotNull(options.SelectedMetrics);
        Assert.Equal(2, options.SelectedMetrics.Count);
        Assert.Contains("llm_relevance", options.SelectedMetrics);
        Assert.Contains("code_tool_success", options.SelectedMetrics);
    }

    [Fact]
    public void EvaluationOptions_SelectedMetrics_CanBeEmpty()
    {
        var options = new EvaluationOptions
        {
            SelectedMetrics = Array.Empty<string>()
        };

        Assert.NotNull(options.SelectedMetrics);
        Assert.Empty(options.SelectedMetrics);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLI PARSING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MetricsParsing_CommaSeparated_SplitsCorrectly()
    {
        // Simulate the CLI parsing logic from EvalCommand
        var metricsArg = "llm_relevance,code_tool_success,llm_faithfulness";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal(3, parsed.Count);
        Assert.Equal("llm_relevance", parsed[0]);
        Assert.Equal("code_tool_success", parsed[1]);
        Assert.Equal("llm_faithfulness", parsed[2]);
    }

    [Fact]
    public void MetricsParsing_WithSpaces_TrimsCorrectly()
    {
        var metricsArg = " llm_relevance , code_tool_success , llm_faithfulness ";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal(3, parsed.Count);
        Assert.Equal("llm_relevance", parsed[0]);
        Assert.Equal("code_tool_success", parsed[1]);
        Assert.Equal("llm_faithfulness", parsed[2]);
    }

    [Fact]
    public void MetricsParsing_SingleMetric_Works()
    {
        var metricsArg = "llm_relevance";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Single(parsed);
        Assert.Equal("llm_relevance", parsed[0]);
    }

    [Fact]
    public void MetricsParsing_EmptyEntries_Ignored()
    {
        var metricsArg = "llm_relevance,,code_tool_success,";
        var parsed = metricsArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Assert.Equal(2, parsed.Count);
    }
}
