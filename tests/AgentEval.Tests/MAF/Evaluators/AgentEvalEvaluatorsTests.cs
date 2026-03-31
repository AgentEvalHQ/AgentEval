// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.MAF.Evaluators;
using AgentEval.Metrics.Agentic;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.MAF.Evaluators;

public class AgentEvalEvaluatorsTests
{
    // We can't construct LLM-dependent bundles without a real IChatClient,
    // so we test the code-based bundles and individual factories.

    [Fact]
    public void Agentic_WithNoArgs_ReturnsEvaluatorWithOneMetric()
    {
        var evaluator = AgentEvalEvaluators.Agentic();

        Assert.Equal(1, evaluator.MetricCount);
        Assert.Contains("code_tool_success", evaluator.MetricNames);
    }

    [Fact]
    public void Agentic_WithExpectedTools_ReturnsEvaluatorWithTwoMetrics()
    {
        var evaluator = AgentEvalEvaluators.Agentic(["SearchFlights", "BookHotel"]);

        Assert.Equal(2, evaluator.MetricCount);
        Assert.Contains("code_tool_success", evaluator.MetricNames);
        Assert.Contains("code_tool_selection", evaluator.MetricNames);
    }

    [Fact]
    public void ToolSuccess_ReturnsAdapterWrappingToolSuccessMetric()
    {
        var evaluator = AgentEvalEvaluators.ToolSuccess();

        Assert.IsType<AgentEvalMetricAdapter>(evaluator);
        var adapter = (AgentEvalMetricAdapter)evaluator;
        Assert.Equal("code_tool_success", adapter.MetricName);
    }

    [Fact]
    public void Custom_WithArbitraryMetrics_CreatesEvaluator()
    {
        var evaluator = AgentEvalEvaluators.Custom(
            new ToolSuccessMetric(),
            new ToolSelectionMetric(["tool1"]));

        Assert.Equal(2, evaluator.MetricCount);
    }

    [Fact]
    public void Adapt_WrapsAnyIMetricAsIEvaluator()
    {
        var metric = new ToolSuccessMetric();
        var evaluator = AgentEvalEvaluators.Adapt(metric);

        Assert.IsType<AgentEvalMetricAdapter>(evaluator);
    }
}
