// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>
/// Performance benchmark for measuring agent latency, throughput, and cost.
/// </summary>
public class PerformanceBenchmark
{
    private readonly IEvaluableAgent _agent;
    private readonly PerformanceBenchmarkOptions _options;
    
    public PerformanceBenchmark(IEvaluableAgent agent, PerformanceBenchmarkOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new PerformanceBenchmarkOptions();
    }
    
    /// <summary>
    /// Run a latency benchmark measuring response times.
    /// </summary>
    public async Task<LatencyBenchmarkResult> RunLatencyBenchmarkAsync(
        string prompt,
        int iterations = 5,
        int warmupIterations = 1,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TimeSpan>();
        var firstTokenTimes = new List<TimeSpan>();
        var errors = new List<Exception>();
        
        // Warmup
        for (int i = 0; i < warmupIterations; i++)
        {
            try
            {
                await _agent.InvokeAsync(prompt, cancellationToken);
            }
            catch
            {
                // Ignore warmup errors
            }
        }
        
        // Actual runs
        for (int i = 0; i < iterations; i++)
        {
            if (_options.Verbose)
            {
                Console.WriteLine($"   Running iteration {i + 1}/{iterations}...");
            }
            
            try
            {
                var startTime = DateTimeOffset.UtcNow;
                TimeSpan? firstTokenTime = null;
                
                if (_agent is IStreamableAgent streamable)
                {
                    bool isFirst = true;
                    await foreach (var chunk in streamable.InvokeStreamingAsync(prompt, cancellationToken))
                    {
                        if (isFirst && !string.IsNullOrEmpty(chunk.Text))
                        {
                            firstTokenTime = DateTimeOffset.UtcNow - startTime;
                            isFirst = false;
                        }
                    }
                }
                else
                {
                    await _agent.InvokeAsync(prompt, cancellationToken);
                }
                
                var totalTime = DateTimeOffset.UtcNow - startTime;
                results.Add(totalTime);
                
                if (firstTokenTime.HasValue)
                {
                    firstTokenTimes.Add(firstTokenTime.Value);
                }
                
                // Delay between iterations
                if (i < iterations - 1 && _options.DelayBetweenIterations > TimeSpan.Zero)
                {
                    await Task.Delay(_options.DelayBetweenIterations, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
        
        return new LatencyBenchmarkResult
        {
            AgentName = _agent.Name,
            Prompt = prompt,
            Iterations = iterations,
            SuccessfulIterations = results.Count,
            Errors = errors,
            
            MeanLatency = results.Count > 0 
                ? TimeSpan.FromMilliseconds(results.Average(t => t.TotalMilliseconds)) 
                : TimeSpan.Zero,
            
            MinLatency = results.Count > 0 
                ? results.Min() 
                : TimeSpan.Zero,
            
            MaxLatency = results.Count > 0 
                ? results.Max() 
                : TimeSpan.Zero,
            
            P50Latency = CalculatePercentile(results, 50),
            P90Latency = CalculatePercentile(results, 90),
            P99Latency = CalculatePercentile(results, 99),
            
            MeanTimeToFirstToken = firstTokenTimes.Count > 0 
                ? TimeSpan.FromMilliseconds(firstTokenTimes.Average(t => t.TotalMilliseconds)) 
                : null,
            
            AllLatencies = results
        };
    }
    
    /// <summary>
    /// Run a latency benchmark across multiple prompts, aggregating all timing results.
    /// Uses varied prompts to avoid server-side caching effects.
    /// </summary>
    /// <param name="prompts">Collection of prompts to benchmark. At least one required.</param>
    /// <param name="iterationsPerPrompt">Number of iterations per prompt. Default: 3.</param>
    /// <param name="warmupIterations">Number of warmup iterations (using the first prompt). Default: 1.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>Aggregated latency results across all prompts.</returns>
    public async Task<LatencyBenchmarkResult> RunLatencyBenchmarkAsync(
        IEnumerable<string> prompts,
        int iterationsPerPrompt = 3,
        int warmupIterations = 1,
        CancellationToken cancellationToken = default)
    {
        var promptList = prompts.ToList();
        if (promptList.Count == 0)
            throw new ArgumentException("At least one prompt is required.", nameof(prompts));

        var allLatencies = new List<TimeSpan>();
        var allTtft = new List<TimeSpan>();
        var allErrors = new List<Exception>();

        // Warmup with first prompt only
        for (int i = 0; i < warmupIterations; i++)
        {
            try { await _agent.InvokeAsync(promptList[0], cancellationToken); }
            catch { /* warmup errors ignored */ }
        }

        // Run each prompt (without additional warmup since we already warmed up)
        foreach (var prompt in promptList)
        {
            var result = await RunLatencyBenchmarkAsync(
                prompt, iterationsPerPrompt, warmupIterations: 0, cancellationToken);
            allLatencies.AddRange(result.AllLatencies);
            allErrors.AddRange(result.Errors);
            if (result.MeanTimeToFirstToken.HasValue)
                allTtft.Add(result.MeanTimeToFirstToken.Value);
        }

        return new LatencyBenchmarkResult
        {
            AgentName = _agent.Name,
            Prompt = $"[{promptList.Count} prompts × {iterationsPerPrompt} iterations]",
            Iterations = promptList.Count * iterationsPerPrompt,
            SuccessfulIterations = allLatencies.Count,
            Errors = allErrors,
            MeanLatency = allLatencies.Count > 0
                ? TimeSpan.FromMilliseconds(allLatencies.Average(t => t.TotalMilliseconds))
                : TimeSpan.Zero,
            MinLatency = allLatencies.Count > 0 ? allLatencies.Min() : TimeSpan.Zero,
            MaxLatency = allLatencies.Count > 0 ? allLatencies.Max() : TimeSpan.Zero,
            P50Latency = CalculatePercentile(allLatencies, 50),
            P90Latency = CalculatePercentile(allLatencies, 90),
            P99Latency = CalculatePercentile(allLatencies, 99),
            MeanTimeToFirstToken = allTtft.Count > 0
                ? TimeSpan.FromMilliseconds(allTtft.Average(t => t.TotalMilliseconds))
                : null,
            AllLatencies = allLatencies
        };
    }
    
    /// <summary>
    /// Run a throughput benchmark measuring requests per second.
    /// </summary>
    public async Task<ThroughputBenchmarkResult> RunThroughputBenchmarkAsync(
        string prompt,
        int concurrentRequests = 5,
        TimeSpan duration = default,
        CancellationToken cancellationToken = default)
    {
        if (duration == default)
        {
            duration = TimeSpan.FromSeconds(10);
        }
        
        var completedRequests = 0;
        var errors = new List<Exception>();
        var latencies = new List<TimeSpan>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        if (_options.Verbose)
        {
            Console.WriteLine($"   Running throughput test for {duration.TotalSeconds}s with {concurrentRequests} concurrent requests...");
        }
        
        var startTime = DateTimeOffset.UtcNow;
        
        // Create worker tasks
        var workers = Enumerable.Range(0, concurrentRequests).Select(async _ =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var requestStart = DateTimeOffset.UtcNow;
                    await _agent.InvokeAsync(prompt, cts.Token);
                    var requestEnd = DateTimeOffset.UtcNow;
                    
                    Interlocked.Increment(ref completedRequests);
                    lock (latencies)
                    {
                        latencies.Add(requestEnd - requestStart);
                    }
                    
                    // Yield to allow cancellation to propagate. Without this,
                    // agents that complete synchronously (e.g. Task.FromResult)
                    // would spin the loop indefinitely without ever yielding
                    // control, preventing Task.Delay from signalling cancellation.
                    await Task.Yield();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }

                    // Yield on error path too — without this, agents that throw
                    // synchronously (e.g. Task.FromException or direct throw)
                    // spin the loop without ever yielding control, preventing
                    // Task.Delay from signalling cancellation → deadlock.
                    await Task.Yield();
                }
            }
        }).ToList();
        
        // Wait for duration
        await Task.Delay(duration, cancellationToken);
        cts.Cancel();
        
        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            // Expected cancellation
        }
        
        var endTime = DateTimeOffset.UtcNow;
        var actualDuration = endTime - startTime;
        
        return new ThroughputBenchmarkResult
        {
            AgentName = _agent.Name,
            Prompt = prompt,
            ConcurrentRequests = concurrentRequests,
            Duration = actualDuration,
            CompletedRequests = completedRequests,
            ErrorCount = errors.Count,
            Errors = errors,
            RequestsPerSecond = completedRequests / actualDuration.TotalSeconds,
            MeanLatency = latencies.Count > 0 
                ? TimeSpan.FromMilliseconds(latencies.Average(t => t.TotalMilliseconds)) 
                : TimeSpan.Zero
        };
    }
    
    /// <summary>
    /// Run a cost benchmark measuring token usage and estimated cost.
    /// </summary>
    public async Task<CostBenchmarkResult> RunCostBenchmarkAsync(
        IEnumerable<string> prompts,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string Prompt, AgentResponse Response)>();
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var errors = new List<Exception>();
        
        foreach (var prompt in prompts)
        {
            try
            {
                var response = await _agent.InvokeAsync(prompt, cancellationToken);
                results.Add((prompt, response));
                
                if (response.TokenUsage != null)
                {
                    totalInputTokens += response.TokenUsage.InputTokens;
                    totalOutputTokens += response.TokenUsage.OutputTokens;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
        
        var pricing = ModelPricing.GetPricing(modelName);
        var estimatedCost = pricing != null
            ? (totalInputTokens / 1_000_000m * pricing.Value.InputPricePerMillion) +
              (totalOutputTokens / 1_000_000m * pricing.Value.OutputPricePerMillion)
            : (decimal?)null;
        
        return new CostBenchmarkResult
        {
            AgentName = _agent.Name,
            ModelName = modelName,
            TotalPrompts = results.Count + errors.Count,
            SuccessfulPrompts = results.Count,
            Errors = errors,
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            TotalTokens = totalInputTokens + totalOutputTokens,
            EstimatedCostUSD = estimatedCost,
            AverageInputTokensPerPrompt = results.Count > 0 
                ? (double)totalInputTokens / results.Count 
                : 0,
            AverageOutputTokensPerPrompt = results.Count > 0 
                ? (double)totalOutputTokens / results.Count 
                : 0
        };
    }
    
    private static TimeSpan CalculatePercentile(List<TimeSpan> values, int percentile)
    {
        if (values.Count == 0) return TimeSpan.Zero;
        
        var sorted = values.OrderBy(t => t).ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Count - 1));
        
        return sorted[index];
    }
}

/// <summary>
/// Options for performance benchmarks.
/// </summary>
public class PerformanceBenchmarkOptions
{
    /// <summary>
    /// Whether to print progress to console.
    /// </summary>
    public bool Verbose { get; set; } = true;
    
    /// <summary>
    /// Delay between benchmark iterations.
    /// </summary>
    public TimeSpan DelayBetweenIterations { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Results from a latency benchmark.
/// </summary>
public class LatencyBenchmarkResult
{
    public required string AgentName { get; init; }
    public required string Prompt { get; init; }
    public int Iterations { get; init; }
    public int SuccessfulIterations { get; init; }
    public IReadOnlyList<Exception> Errors { get; init; } = [];
    
    public TimeSpan MeanLatency { get; init; }
    public TimeSpan MinLatency { get; init; }
    public TimeSpan MaxLatency { get; init; }
    public TimeSpan P50Latency { get; init; }
    public TimeSpan P90Latency { get; init; }
    public TimeSpan P99Latency { get; init; }
    public TimeSpan? MeanTimeToFirstToken { get; init; }
    
    public IReadOnlyList<TimeSpan> AllLatencies { get; init; } = [];
    
    public override string ToString() =>
        $"Latency Benchmark: {AgentName}\n" +
        $"  Iterations: {SuccessfulIterations}/{Iterations}\n" +
        string.Create(CultureInfo.InvariantCulture, $"  Mean: {MeanLatency.TotalMilliseconds:F0}ms | Min: {MinLatency.TotalMilliseconds:F0}ms | Max: {MaxLatency.TotalMilliseconds:F0}ms\n") +
        string.Create(CultureInfo.InvariantCulture, $"  P50: {P50Latency.TotalMilliseconds:F0}ms | P90: {P90Latency.TotalMilliseconds:F0}ms | P99: {P99Latency.TotalMilliseconds:F0}ms") +
        (MeanTimeToFirstToken.HasValue ? string.Create(CultureInfo.InvariantCulture, $"\n  Mean TTFT: {MeanTimeToFirstToken.Value.TotalMilliseconds:F0}ms") : "");
}

/// <summary>
/// Results from a throughput benchmark.
/// </summary>
public class ThroughputBenchmarkResult
{
    public required string AgentName { get; init; }
    public required string Prompt { get; init; }
    public int ConcurrentRequests { get; init; }
    public TimeSpan Duration { get; init; }
    public int CompletedRequests { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<Exception> Errors { get; init; } = [];
    public double RequestsPerSecond { get; init; }
    public TimeSpan MeanLatency { get; init; }
    
    public override string ToString() =>
        $"Throughput Benchmark: {AgentName}\n" +
        string.Create(CultureInfo.InvariantCulture, $"  Duration: {Duration.TotalSeconds:F1}s | Concurrent: {ConcurrentRequests}\n") +
        $"  Completed: {CompletedRequests} | Errors: {ErrorCount}\n" +
        string.Create(CultureInfo.InvariantCulture, $"  RPS: {RequestsPerSecond:F2} | Mean Latency: {MeanLatency.TotalMilliseconds:F0}ms");
}

/// <summary>
/// Results from a cost benchmark.
/// </summary>
public class CostBenchmarkResult
{
    public required string AgentName { get; init; }
    public required string ModelName { get; init; }
    public int TotalPrompts { get; init; }
    public int SuccessfulPrompts { get; init; }
    public IReadOnlyList<Exception> Errors { get; init; } = [];
    
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public int TotalTokens { get; init; }
    public decimal? EstimatedCostUSD { get; init; }
    
    public double AverageInputTokensPerPrompt { get; init; }
    public double AverageOutputTokensPerPrompt { get; init; }
    
    public override string ToString() =>
        $"Cost Benchmark: {AgentName} ({ModelName})\n" +
        $"  Prompts: {SuccessfulPrompts}/{TotalPrompts}\n" +
        $"  Tokens: {TotalInputTokens} input + {TotalOutputTokens} output = {TotalTokens} total\n" +
        string.Create(CultureInfo.InvariantCulture, $"  Avg per prompt: {AverageInputTokensPerPrompt:F0} in / {AverageOutputTokensPerPrompt:F0} out\n") +
        (EstimatedCostUSD.HasValue ? string.Create(CultureInfo.InvariantCulture, $"  Estimated Cost: ${EstimatedCostUSD:F6}") : "  Estimated Cost: Unknown pricing");
}
