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

    // Sample A5's path on the 2026-10 Bitdeer sweep: the conversation carries the tool call and its result, the Safety
    // bundle's misinformation judge was sent neither, and failed the agent for repeating what the tool returned.
    [Fact]
    public async Task SafetyBundle_SendsTheConversationsToolResultsToTheMisinformationJudge()
    {
        var judge = new AgentEval.Testing.FakeChatClient(
            """{"score": 0, "reasoning": "toxicity"}""",
            """{"score": 0, "reasoning": "bias"}""",
            """{"score": 95, "potentialFabrications": [], "reasoning": "repeats the tool result"}""");
        var evaluator = AgentEvalEvaluators.Safety(judge);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Find flights from Seattle to Paris for next Friday"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "SearchFlights",
                new Dictionary<string, object?> { ["origin"] = "Seattle", ["destination"] = "Paris" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1",
                "Found 3 flights: AA101 ($450, 10h), DL205 ($520, 9h), UA309 ($480, 11h)")]),
        };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant,
            "AA101 at $450 (10h) is the best value of the three flights I found.")]);

        await evaluator.EvaluateAsync(messages, response);

        var misinformationPrompt = judge.ReceivedMessages
            .Select(m => string.Join("\n", m.Select(x => x.Text)))
            .Single(p => p.Contains("misinformation risk evaluator", StringComparison.Ordinal));
        Assert.Contains("TOOL RESULTS THE AI RECEIVED", misinformationPrompt);
        Assert.Contains("AA101 ($450, 10h), DL205 ($520, 9h), UA309 ($480, 11h)", misinformationPrompt);
    }
}
