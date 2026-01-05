// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Models;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests to verify thread-safety and concurrent execution behavior.
/// </summary>
public class ConcurrencyTests
{
    private static ChatMessage[] CreateMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    #region FakeChatClient Concurrency

    [Fact]
    public async Task FakeChatClient_ConcurrentCalls_MaintainsCorrectCallCount()
    {
        // Arrange
        var responses = Enumerable.Range(0, 100)
            .Select(i => $"Response {i}")
            .ToArray();
        var fakeChatClient = new FakeChatClient(responses);
        
        // Act - make 100 concurrent calls
        var tasks = Enumerable.Range(0, 100)
            .Select(i => fakeChatClient.GetResponseAsync(CreateMessages($"Message {i}")))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        // Assert
        Assert.Equal(100, fakeChatClient.CallCount);
    }

    [Fact]
    public async Task FakeChatClient_ConcurrentCalls_RecordsAllMessages()
    {
        // Arrange
        var responses = Enumerable.Range(0, 50)
            .Select(i => $"Response {i}")
            .ToArray();
        var fakeChatClient = new FakeChatClient(responses);
        
        // Act - make 50 concurrent calls
        var tasks = Enumerable.Range(0, 50)
            .Select(i => fakeChatClient.GetResponseAsync(CreateMessages($"Message {i}")))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        // Assert - all messages should be recorded
        Assert.Equal(50, fakeChatClient.ReceivedMessages.Count);
    }

    #endregion

    #region Metric Evaluation Concurrency

    [Fact]
    public async Task FaithfulnessMetric_ConcurrentEvaluations_AllComplete()
    {
        // Arrange
        var responses = Enumerable.Range(0, 20)
            .Select(i => $$"""{"score": {{50 + i}}, "reasoning": "Test {{i}}"}""")
            .ToArray();
        var fakeChatClient = new FakeChatClient(responses);
        var metric = new FaithfulnessMetric(fakeChatClient);
        
        var contexts = Enumerable.Range(0, 20)
            .Select(i => new EvaluationContext
            {
                Input = $"Question {i}",
                Output = $"Answer {i}",
                Context = $"Context {i}"
            })
            .ToArray();
        
        // Act - evaluate all contexts concurrently
        var tasks = contexts.Select(ctx => metric.EvaluateAsync(ctx)).ToArray();
        var results = await Task.WhenAll(tasks);
        
        // Assert
        Assert.Equal(20, results.Length);
        Assert.All(results, r => Assert.True(r.Score >= 0 && r.Score <= 100));
        Assert.Equal(20, fakeChatClient.CallCount);
    }

    [Fact]
    public async Task MultipleMetrics_ConcurrentEvaluation_IndependentResults()
    {
        // Arrange - create separate chat clients for each metric
        var faithfulnessClient = new FakeChatClient("""{"score": 85, "reasoning": "Faithful"}""");
        var relevanceClient = new FakeChatClient("""{"score": 75, "reasoning": "Relevant"}""");
        
        var faithfulnessMetric = new FaithfulnessMetric(faithfulnessClient);
        var relevanceMetric = new RelevanceMetric(relevanceClient);
        
        var context = new EvaluationContext
        {
            Input = "What is AI?",
            Output = "AI is artificial intelligence.",
            Context = "AI stands for artificial intelligence."
        };
        
        // Act - evaluate both metrics concurrently
        var faithfulnessTask = faithfulnessMetric.EvaluateAsync(context);
        var relevanceTask = relevanceMetric.EvaluateAsync(context);
        
        var results = await Task.WhenAll(faithfulnessTask, relevanceTask);
        
        // Assert - each metric should have independent results
        Assert.Equal(85, results[0].Score);
        Assert.Equal(75, results[1].Score);
        Assert.Equal(1, faithfulnessClient.CallCount);
        Assert.Equal(1, relevanceClient.CallCount);
    }

    #endregion

    #region ToolCallTimeline Concurrency

    [Fact]
    public async Task ToolCallTimeline_ConcurrentAddInvocations_AllInvocationsRecorded()
    {
        // Arrange
        var timeline = new ToolCallTimeline();
        var lockObj = new object();
        
        // Act - add 100 tool invocations concurrently
        // Note: ToolCallTimeline uses a List internally, so we use external sync
        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() =>
            {
                lock (lockObj)
                {
                    timeline.AddInvocation(new ToolInvocation
                    {
                        ToolName = $"Tool{i}",
                        StartTime = TimeSpan.FromMilliseconds(i * 10),
                        Duration = TimeSpan.FromMilliseconds(i),
                        Succeeded = true
                    });
                }
            }))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        // Assert - all invocations should be recorded
        Assert.Equal(100, timeline.Invocations.Count);
    }

    #endregion

    #region MetricRegistry Concurrency

    [Fact]
    public async Task MetricRegistry_ConcurrentRegistration_HandlesConflictsGracefully()
    {
        // Arrange
        var registry = new MetricRegistry();
        var fakeChatClient = new FakeChatClient();
        var successCount = 0;
        var exceptionCount = 0;
        
        // Act - register same metric concurrently (will throw on duplicates)
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() =>
            {
                try
                {
                    registry.Register(new FaithfulnessMetric(fakeChatClient));
                    Interlocked.Increment(ref successCount);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref exceptionCount);
                }
            }))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        // Assert - exactly one registration should succeed, rest should throw
        Assert.Equal(1, successCount);
        Assert.Equal(9, exceptionCount);
        Assert.Single(registry.GetAll());
    }

    [Fact]
    public async Task MetricRegistry_ConcurrentReadWrite_NoExceptions()
    {
        // Arrange
        var registry = new MetricRegistry();
        var fakeChatClient = new FakeChatClient();
        var exceptions = new List<Exception>();
        var metricCounter = 0;
        
        // Act - concurrent reads and writes with unique metric names
        var tasks = new List<Task>();
        
        for (int i = 0; i < 50; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    if (index % 2 == 0)
                    {
                        // Register with unique name to avoid conflicts
                        var metricNum = Interlocked.Increment(ref metricCounter);
                        registry.Register($"Metric_{metricNum}", new FaithfulnessMetric(fakeChatClient));
                    }
                    else
                    {
                        _ = registry.GetAll().ToList();
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));
        }
        
        await Task.WhenAll(tasks.ToArray());
        
        // Assert - no exceptions during concurrent access
        Assert.Empty(exceptions);
    }

    #endregion

    #region RetryPolicy Concurrency

    [Fact]
    public async Task RetryPolicy_ConcurrentRetries_IndependentBehavior()
    {
        // Arrange
        var policy = new RetryPolicy { MaxRetries = 3, InitialDelayMs = 10 };
        var callCount = 0;
        
        // Act - execute multiple retry operations concurrently
        var tasks = Enumerable.Range(0, 5)
            .Select(async i =>
            {
                return await policy.ExecuteAsync(async () =>
                {
                    Interlocked.Increment(ref callCount);
                    await Task.Delay(1);
                    return i;
                });
            })
            .ToArray();
        
        var results = await Task.WhenAll(tasks);
        
        // Assert - all operations should complete
        Assert.Equal(5, results.Length);
        Assert.Contains(0, results);
        Assert.Contains(4, results);
    }

    #endregion
}
