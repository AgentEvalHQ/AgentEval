// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;
using AgentEval.MAF.Evaluators;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.MAF.Evaluators;

public class AgentEvalMetricAdapterTests
{
    /// <summary>
    /// Mock metric that records the EvaluationContext it receives for assertions.
    /// </summary>
    private class CapturingMetric : IMetric
    {
        public string Name => "test_capturing_metric";
        public string Description => "Test metric that captures context";
        public AgentEvalEvaluationContext? CapturedContext { get; private set; }
        public MetricResult ResultToReturn { get; set; } = MetricResult.Pass("test_capturing_metric", 85, "Good");

        public Task<MetricResult> EvaluateAsync(AgentEvalEvaluationContext context, CancellationToken cancellationToken = default)
        {
            CapturedContext = context;
            return Task.FromResult(ResultToReturn);
        }
    }

    [Fact]
    public async Task EvaluateAsync_ExtractsInputOutputCorrectly()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is the weather?"),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "It is sunny.")]);

        await adapter.EvaluateAsync(messages, response);

        Assert.NotNull(metric.CapturedContext);
        Assert.Equal("What is the weather?", metric.CapturedContext!.Input);
        Assert.Equal("It is sunny.", metric.CapturedContext.Output);
    }

    [Fact]
    public async Task EvaluateAsync_WithToolCalls_ExtractsToolUsage()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search flights"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "SearchFlights")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Found flights.")]);

        await adapter.EvaluateAsync(messages, response);

        Assert.NotNull(metric.CapturedContext?.ToolUsage);
        Assert.Equal(1, metric.CapturedContext!.ToolUsage!.Count);
        Assert.Equal("SearchFlights", metric.CapturedContext.ToolUsage.Calls[0].Name);
    }

    [Fact]
    public async Task EvaluateAsync_WithRAGContext_PassesContextToMetric()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);
        var additionalContext = new[] { new AgentEvalRAGContext("Retrieved document text") };

        await adapter.EvaluateAsync(messages, response, additionalContext: additionalContext);

        Assert.Equal("Retrieved document text", metric.CapturedContext?.Context);
    }

    [Fact]
    public async Task EvaluateAsync_WithGroundTruth_PassesGroundTruthToMetric()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);
        var additionalContext = new[] { new AgentEvalGroundTruthContext("Expected answer") };

        await adapter.EvaluateAsync(messages, response, additionalContext: additionalContext);

        Assert.Equal("Expected answer", metric.CapturedContext?.GroundTruth);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpectedTools_PassesExpectedToolsToMetric()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);
        var additionalContext = new[] { new AgentEvalExpectedToolsContext(["ToolA", "ToolB"]) };

        await adapter.EvaluateAsync(messages, response, additionalContext: additionalContext);

        Assert.NotNull(metric.CapturedContext?.ExpectedTools);
        Assert.Equal(2, metric.CapturedContext!.ExpectedTools!.Count);
        Assert.Contains("ToolA", metric.CapturedContext.ExpectedTools);
        Assert.Contains("ToolB", metric.CapturedContext.ExpectedTools);
    }

    [Fact]
    public async Task EvaluateAsync_WithNoAdditionalContext_NullContextAndGroundTruth()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);

        await adapter.EvaluateAsync(messages, response);

        Assert.Null(metric.CapturedContext?.Context);
        Assert.Null(metric.CapturedContext?.GroundTruth);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsConvertedMEAIResult()
    {
        var metric = new CapturingMetric { ResultToReturn = MetricResult.Pass("test_capturing_metric", 100, "Perfect") };
        var adapter = new AgentEvalMetricAdapter(metric);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);

        var result = await adapter.EvaluateAsync(messages, response);

        Assert.True(result.Metrics.ContainsKey("test_capturing_metric"));
        var numeric = Assert.IsType<NumericMetric>(result.Metrics["test_capturing_metric"]);
        Assert.Equal(5.0, numeric.Value!.Value, precision: 1);
    }

    [Fact]
    public void EvaluationMetricNames_ReturnsSingleMetricName()
    {
        var metric = new CapturingMetric();
        var adapter = new AgentEvalMetricAdapter(metric);

        Assert.Single(adapter.EvaluationMetricNames);
        Assert.Contains("test_capturing_metric", adapter.EvaluationMetricNames);
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentEvalMetricAdapter(null!));
    }
}
