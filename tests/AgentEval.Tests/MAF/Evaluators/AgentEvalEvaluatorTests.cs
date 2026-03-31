// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;
using AgentEval.MAF.Evaluators;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;

namespace AgentEval.Tests.MAF.Evaluators;

public class AgentEvalEvaluatorTests
{
    private class SimpleMetric : IMetric
    {
        public string Name { get; }
        public string Description => "Test metric";
        public double ScoreToReturn { get; set; } = 80;

        public SimpleMetric(string name) => Name = name;

        public Task<MetricResult> EvaluateAsync(AgentEvalEvaluationContext context, CancellationToken ct = default)
            => Task.FromResult(MetricResult.Pass(Name, ScoreToReturn));
    }

    private class FailingMetric : IMetric
    {
        public string Name => "failing_metric";
        public string Description => "Always throws";

        public Task<MetricResult> EvaluateAsync(AgentEvalEvaluationContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("Metric crashed");
    }

    [Fact]
    public async Task EvaluateAsync_WithMultipleMetrics_ReturnsAllMetricResults()
    {
        var evaluator = new AgentEvalEvaluator([
            new SimpleMetric("metric_a") { ScoreToReturn = 90 },
            new SimpleMetric("metric_b") { ScoreToReturn = 70 },
        ]);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);

        var result = await evaluator.EvaluateAsync(messages, response);

        Assert.Equal(2, result.Metrics.Count);
        Assert.True(result.Metrics.ContainsKey("metric_a"));
        Assert.True(result.Metrics.ContainsKey("metric_b"));
    }

    [Fact]
    public async Task EvaluateAsync_WithFailingMetric_ContinuesAndIncludesFailure()
    {
        var evaluator = new AgentEvalEvaluator([
            new SimpleMetric("good_metric"),
            new FailingMetric(),
            new SimpleMetric("another_good"),
        ]);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);

        var result = await evaluator.EvaluateAsync(messages, response);

        Assert.Equal(3, result.Metrics.Count);
        Assert.True(result.Metrics.ContainsKey("good_metric"));
        Assert.True(result.Metrics.ContainsKey("failing_metric"));
        Assert.True(result.Metrics.ContainsKey("another_good"));

        // Failed metric should have low score
        var failedMetric = Assert.IsType<NumericMetric>(result.Metrics["failing_metric"]);
        Assert.True(failedMetric.Interpretation!.Failed);
    }

    [Fact]
    public async Task EvaluateAsync_WithCancellation_ThrowsOperationCanceled()
    {
        var evaluator = new AgentEvalEvaluator([new SimpleMetric("test")]);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Q") };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "A")]);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => evaluator.EvaluateAsync(messages, response, cancellationToken: cts.Token).AsTask());
    }

    [Fact]
    public void MetricCount_ReturnsCorrectCount()
    {
        var evaluator = new AgentEvalEvaluator([
            new SimpleMetric("a"),
            new SimpleMetric("b"),
            new SimpleMetric("c"),
        ]);

        Assert.Equal(3, evaluator.MetricCount);
    }

    [Fact]
    public void EvaluationMetricNames_ReturnsAllMetricNames()
    {
        var evaluator = new AgentEvalEvaluator([
            new SimpleMetric("alpha"),
            new SimpleMetric("beta"),
        ]);

        var names = evaluator.EvaluationMetricNames;

        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void Constructor_WithEmptyMetrics_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new AgentEvalEvaluator([]));
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentEvalEvaluator(null!));
    }
}
