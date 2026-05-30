// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for <see cref="PerformanceBenchmark"/> — latency, throughput, cost, and multi-prompt overloads.
/// </summary>
public class PerformanceBenchmarkTests
{
    #region Latency Benchmark Tests

    [Fact]
    public async Task RunLatencyBenchmarkAsync_ReturnsCorrectIterationCount()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act
        var result = await benchmark.RunLatencyBenchmarkAsync("test prompt",
            iterations: 3, warmupIterations: 0);

        // Assert
        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, result.SuccessfulIterations);
        Assert.Equal("TestAgent", result.AgentName);
        Assert.Equal("test prompt", result.Prompt);
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_CalculatesPercentiles()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act
        var result = await benchmark.RunLatencyBenchmarkAsync("test",
            iterations: 10, warmupIterations: 0);

        // Assert
        Assert.True(result.P50Latency >= TimeSpan.Zero);
        Assert.True(result.P90Latency >= result.P50Latency);
        Assert.True(result.P99Latency >= result.P90Latency);
        Assert.True(result.MeanLatency >= TimeSpan.Zero);
        Assert.Equal(10, result.AllLatencies.Count);
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_WithWarmup_ExcludesWarmupFromResults()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act
        var result = await benchmark.RunLatencyBenchmarkAsync("test",
            iterations: 3, warmupIterations: 2);

        // Assert — warmup runs are excluded from result count
        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, result.SuccessfulIterations);
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_MultiplePrompts_AggregatesResults()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act
        var result = await benchmark.RunLatencyBenchmarkAsync(
            new[] { "prompt1", "prompt2", "prompt3" },
            iterationsPerPrompt: 2, warmupIterations: 0);

        // Assert — 3 prompts × 2 iterations = 6 total
        Assert.Equal(6, result.Iterations);
        Assert.Equal(6, result.SuccessfulIterations);
        Assert.Equal(6, result.AllLatencies.Count);
        Assert.Contains("3 prompts", result.Prompt);
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_MultiplePrompts_EmptyList_Throws()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false });

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => benchmark.RunLatencyBenchmarkAsync(Array.Empty<string>()));
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_MultiplePrompts_WithWarmup_WorksCorrectly()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act — 2 prompts × 2 iterations, 1 warmup
        var result = await benchmark.RunLatencyBenchmarkAsync(
            new[] { "prompt1", "prompt2" },
            iterationsPerPrompt: 2, warmupIterations: 1);

        // Assert — 4 total iterations (warmup done once before all prompts)
        Assert.Equal(4, result.Iterations);
        Assert.Equal(4, result.SuccessfulIterations);
        Assert.Equal(4, result.AllLatencies.Count);
        Assert.Contains("2 prompts", result.Prompt);
    }

    #endregion

    #region Throughput Benchmark Tests

    [Fact]
    public async Task RunThroughputBenchmarkAsync_ReturnsPositiveRPS()
    {
        // Arrange — use a very short duration because MockTestableAgent returns instantly
        // (Task.FromResult), so the worker loop would otherwise spin millions of times
        // in a tight loop, causing excessive CPU and memory usage.
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false });

        // Act — 200ms is enough to prove RPS > 0 without burning CPU
        var result = await benchmark.RunThroughputBenchmarkAsync(
            "test", concurrentRequests: 2, duration: TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.True(result.CompletedRequests > 0);
        Assert.True(result.RequestsPerSecond > 0);
        Assert.Equal("TestAgent", result.AgentName);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task RunThroughputBenchmarkAsync_AgentThrows_RecordsErrors()
    {
        // Arrange
        var agent = new ThrowingAgent("FailAgent", new InvalidOperationException("Throughput error"));
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false });

        // Act — short duration to avoid hanging
        var result = await benchmark.RunThroughputBenchmarkAsync(
            "test", concurrentRequests: 2, duration: TimeSpan.FromMilliseconds(200));

        // Assert — errors recorded, no completed requests
        Assert.Equal(0, result.CompletedRequests);
        Assert.True(result.ErrorCount > 0);
        Assert.True(result.Errors.Count > 0);
    }

    #endregion

    #region Cost Benchmark Tests

    [Fact]
    public async Task RunCostBenchmarkAsync_CalculatesTokenUsage()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false });

        // Act
        var result = await benchmark.RunCostBenchmarkAsync(
            new[] { "prompt1", "prompt2" }, "gpt-4o");

        // Assert
        Assert.Equal("TestAgent", result.AgentName);
        Assert.Equal("gpt-4o", result.ModelName);
        Assert.Equal(2, result.TotalPrompts);
        Assert.Equal(2, result.SuccessfulPrompts);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NullAgent_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PerformanceBenchmark(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Applied()
    {
        // Arrange
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var options = new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.FromSeconds(1) };

        // Act
        var benchmark = new PerformanceBenchmark(agent, options);

        // Assert — no exception
        Assert.NotNull(benchmark);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task RunLatencyBenchmarkAsync_AgentThrows_RecordsErrors()
    {
        // Arrange
        var agent = new ThrowingAgent("FailAgent", new InvalidOperationException("Boom"));
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        // Act
        var result = await benchmark.RunLatencyBenchmarkAsync("test",
            iterations: 3, warmupIterations: 0);

        // Assert — all iterations failed
        Assert.Equal(3, result.Iterations);
        Assert.Equal(0, result.SuccessfulIterations);
        Assert.Equal(3, result.Errors.Count);
        Assert.Equal(TimeSpan.Zero, result.MeanLatency);
    }

    [Fact]
    public async Task RunCostBenchmarkAsync_AgentThrows_RecordsErrors()
    {
        // Arrange
        var agent = new ThrowingAgent("FailAgent", new InvalidOperationException("Cost error"));
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false });

        // Act
        var result = await benchmark.RunCostBenchmarkAsync(
            new[] { "prompt1", "prompt2" }, "gpt-4o");

        // Assert
        Assert.Equal(2, result.TotalPrompts);
        Assert.Equal(0, result.SuccessfulPrompts);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(0, result.TotalTokens);
    }

    [Fact]
    public async Task RunLatencyBenchmarkAsync_CancellationRequested_NoSuccessfulIterations()
    {
        // Arrange — agent that respects cancellation by throwing
        var agent = new CancellationRespectingAgent("TestAgent");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act — method catches exceptions internally, so it completes normally but with 0 successes
        var result = await benchmark.RunLatencyBenchmarkAsync("test",
            iterations: 5, warmupIterations: 0, cancellationToken: cts.Token);

        // Assert
        Assert.Equal(0, result.SuccessfulIterations);
        Assert.Equal(5, result.Errors.Count);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Agent that always throws a specified exception (for error handling tests).
    /// </summary>
    private sealed class ThrowingAgent : IEvaluableAgent
    {
        private readonly Exception _exception;
        public string Name { get; }

        public ThrowingAgent(string name, Exception exception)
        {
            Name = name;
            _exception = exception;
        }

        public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
            => Task.FromException<AgentResponse>(_exception);
    }

    /// <summary>
    /// Agent that throws OperationCanceledException when token is cancelled.
    /// </summary>
    private sealed class CancellationRespectingAgent : IEvaluableAgent
    {
        public string Name { get; }

        public CancellationRespectingAgent(string name) => Name = name;

        public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentResponse { Text = "OK" });
        }
    }

    #endregion

    #region Result ToString Tests

    [Fact]
    public void LatencyBenchmarkResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new LatencyBenchmarkResult
        {
            AgentName = "TestAgent",
            Prompt = "Hello",
            Iterations = 5,
            SuccessfulIterations = 5,
            MeanLatency = TimeSpan.FromMilliseconds(150),
            MinLatency = TimeSpan.FromMilliseconds(100),
            MaxLatency = TimeSpan.FromMilliseconds(200),
            P50Latency = TimeSpan.FromMilliseconds(140),
            P90Latency = TimeSpan.FromMilliseconds(190),
            P99Latency = TimeSpan.FromMilliseconds(199)
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("150", str);
        Assert.Contains("5/5", str);
    }

    [Fact]
    public void ThroughputBenchmarkResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new ThroughputBenchmarkResult
        {
            AgentName = "TestAgent",
            Prompt = "Hello",
            ConcurrentRequests = 5,
            Duration = TimeSpan.FromSeconds(10),
            CompletedRequests = 50,
            ErrorCount = 0,
            RequestsPerSecond = 5.0,
            MeanLatency = TimeSpan.FromMilliseconds(200)
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("50", str);
        Assert.Contains("5.00", str);
    }

    [Fact]
    public void CostBenchmarkResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new CostBenchmarkResult
        {
            AgentName = "TestAgent",
            ModelName = "gpt-4o",
            TotalPrompts = 3,
            SuccessfulPrompts = 3,
            TotalInputTokens = 150,
            TotalOutputTokens = 300,
            TotalTokens = 450,
            EstimatedCostUSD = 0.0045m,
            AverageInputTokensPerPrompt = 50,
            AverageOutputTokensPerPrompt = 100
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("gpt-4o", str);
        Assert.Contains("450", str);
    }

    #endregion

    #region Cost pricing-warning (BUG-29)

    [Fact]
    public async Task RunCostBenchmarkAsync_UnknownModelName_EmitsPricingWarning()
    {
        // BUG-29: a typo'd / unrecognised model name yields null pricing. The warning previously
        // fired only when the name equalled the agent name, so the operator's most-likely mistake
        // (e.g. "gpt4o" instead of "gpt-4o") silently produced a $0 pass with no diagnostic.
        var agent = new MockTestableAgent("TestAgent", "Hello");
        var benchmark = new PerformanceBenchmark(agent,
            new PerformanceBenchmarkOptions { Verbose = false, DelayBetweenIterations = TimeSpan.Zero });

        var originalErr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            await benchmark.RunCostBenchmarkAsync(new[] { "prompt" }, modelName: "gpt4o");
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var stderr = captured.ToString();
        Assert.Contains("WARNING", stderr);
        Assert.Contains("gpt4o", stderr); // the unrecognised name is surfaced
    }

    #endregion
}
