// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>
/// Performance benchmark for measuring agent latency, throughput, and cost.
/// </summary>
public class PerformanceBenchmark
{
    private readonly IEvaluableAgent _agent;
    private readonly PerformanceBenchmarkOptions _options;
    private readonly IAgentEvalLogger? _logger;

    public PerformanceBenchmark(IEvaluableAgent agent, PerformanceBenchmarkOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new PerformanceBenchmarkOptions();
        _logger = _options.Logger;
    }

    // ARC-08: route progress/warnings through the injected IAgentEvalLogger so hosts can capture or
    // redirect them. When no logger is supplied, preserve the original Console behaviour exactly —
    // progress to stdout only when Verbose; the critical no-pricing warning to stderr ALWAYS (it is a
    // correctness diagnostic, BUG-29, and must not be silenced by a non-verbose run).
    private void LogProgress(string message)
    {
        if (_logger is not null)
            _logger.Log(LogLevel.Information, message);
        else if (_options.Verbose)
            Console.WriteLine(message);
    }

    private void LogWarning(string message)
    {
        if (_logger is not null)
            _logger.Log(LogLevel.Warning, message);
        else
            Console.Error.WriteLine(message);
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
            LogProgress($"   Running iteration {i + 1}/{iterations}...");

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
        
        LogProgress($"   Running throughput test for {duration.TotalSeconds}s with {concurrentRequests} concurrent requests...");

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
            // Divide by the configured measurement window, not actualDuration: the latter is taken
            // before workers spin up and after the post-cancel drain (Task.WhenAll), so it includes
            // startup scheduling + one in-flight request per worker, systematically under-reporting
            // RPS and able to flip a borderline pass/fail (BUG-51).
            RequestsPerSecond = duration.TotalSeconds > 0
                ? completedRequests / duration.TotalSeconds
                : 0,
            MeanLatency = latencies.Count > 0 
                ? TimeSpan.FromMilliseconds(latencies.Average(t => t.TotalMilliseconds)) 
                : TimeSpan.Zero
        };
    }
    
    /// <summary>
    /// Run a cost benchmark measuring token usage and estimated cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="modelName"/> is the operator-supplied pricing key. The method
    /// will first try that exact value against <see cref="ModelPricing"/>. If no
    /// pricing entry matches and any agent response carried a non-null
    /// <see cref="AgentResponse.ModelId"/>, the lookup is retried using the
    /// first successful response's reported <c>ModelId</c>. This protects the
    /// cost-leaf from a silent $0 / PASS when the caller passes the agent's
    /// display name rather than a real deployment id.
    /// </para>
    /// </remarks>
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

        // P0-1: priority chain for pricing resolution.
        //   1. modelName (operator-supplied; usually opts.CostModelName)
        //   2. first successful response.ModelId (when the framework surfaces it)
        //   3. agent.Name — last resort, with a warning, because agent display
        //      names rarely match a pricing-table key and lead to silent $0 / PASS.
        var resolvedModelName = modelName;
        var pricing = ModelPricing.GetPricing(resolvedModelName);

        if (pricing == null)
        {
            var firstResponseModelId = results
                .Select(r => r.Response.ModelId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            if (!string.IsNullOrWhiteSpace(firstResponseModelId))
            {
                var fallbackPricing = ModelPricing.GetPricing(firstResponseModelId);
                if (fallbackPricing != null)
                {
                    resolvedModelName = firstResponseModelId!;
                    pricing = fallbackPricing;
                }
            }
        }

        // Warn whenever pricing could not be resolved, NOT only when the operator passed
        // nothing (resolvedModelName == agent name). A typo'd/unrecognised CostModelName
        // ('gpt4o' vs 'gpt-4o') also yields null pricing, and the cost leaf then passes at $0 —
        // so the operator's most-likely mistake must still surface this diagnostic (BUG-29).
        if (pricing == null)
        {
            LogWarning(
                $"[perf-cost] WARNING: no pricing entry found for '{resolvedModelName}'. " +
                "Cost will be reported as $0 and the cost leaf will pass by default. " +
                "Check PerformanceBenchmarkEvaluateOptions.CostModelName for a typo (e.g. 'gpt-4o', not 'gpt4o'), " +
                "pass a known model id, or ensure your IEvaluableAgent populates AgentResponse.ModelId.");
        }

        var estimatedCost = pricing != null
            ? (totalInputTokens / 1_000_000m * pricing.Value.InputPricePerMillion) +
              (totalOutputTokens / 1_000_000m * pricing.Value.OutputPricePerMillion)
            : (decimal?)null;

        return new CostBenchmarkResult
        {
            AgentName = _agent.Name,
            ModelName = resolvedModelName,
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
    
    // ─── EvalResult adapter ──────────────────────────────────────────────────

    /// <summary>
    /// Runs latency, throughput, and cost measurements against the prompt(s) in
    /// <paramref name="input"/> and returns a <see cref="EvalResult"/> in the
    /// CompositeEval shape — three leaf results (latency, throughput, cost) combined
    /// by <c>CapByWorst</c> aggregation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Prompt resolution</b>: The adapter derives the list of prompts to run from
    /// <paramref name="input"/> using the following priority order:
    /// <list type="number">
    ///   <item><c>input.Metadata["prompts"]</c> — an <c>IEnumerable&lt;string&gt;</c> or
    ///         comma-separated string of multiple prompts.</item>
    ///   <item><c>input.Query</c> — used as a single prompt when Metadata["prompts"] is absent.</item>
    /// </list>
    /// If neither yields at least one prompt, an <b>empty-input</b> skipped result is returned
    /// immediately instead of producing nonsense measurements.
    /// </para>
    /// <para>
    /// <b>Scoring</b>:
    /// <list type="bullet">
    ///   <item><b>Latency</b> — P99 vs <c>LatencyOptions.P99ThresholdMs</c> (default 5000 ms).
    ///         score = 1 − (p99ms / threshold), clamped to [0, 1]. Lower latency → higher score.</item>
    ///   <item><b>Throughput</b> — RequestsPerSecond vs <c>ThroughputOptions.MinRps</c> (default 0.5 RPS).
    ///         score = min(rps / minRps, 1.0). Higher throughput → higher score.</item>
    ///   <item><b>Cost</b> — EstimatedCostUSD vs <c>CostOptions.MaxCostUSD</c> (default 0.10 USD).
    ///         score = 1 − (cost / maxCost), clamped to [0, 1]. Lower cost → higher score.
    ///         When cost is unknown (no pricing data), score defaults to 1.0 (pass with low severity).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Aggregation</b>: <c>CapByWorst</c> — a critical/high-severity failure in any single leaf
    /// caps the composite. Use <see cref="PerformanceBenchmarkOptions.EvaluateOptions"/> to tune thresholds
    /// before calling this method.
    /// </para>
    /// </remarks>
    /// <param name="input">Eval input. Prompts are drawn from Metadata["prompts"] or Query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>CompositeEval-shaped <see cref="EvalResult"/> with three leaf sub-results.</returns>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ── Resolve prompts ────────────────────────────────────────────────────
        var prompts = ResolvePrompts(input);
        if (prompts.Count == 0)
        {
            // Return a skip result rather than silently producing nonsense metrics.
            return MakeSkippedResult(
                "perf_benchmark",
                "Performance Benchmark",
                "performance",
                "EvalInput contained no resolvable prompts. " +
                "Provide prompts via EvalInput.Query or EvalInput.Metadata[\"prompts\"].");
        }

        var opts = _options.EvaluateOptions ?? PerformanceBenchmarkEvaluateOptions.Default;

        // ── Run all three measurements (sequentially to avoid resource contention) ─
        var latencyResult = await RunLatencyBenchmarkAsync(
            prompts, iterationsPerPrompt: opts.LatencyIterationsPerPrompt,
            warmupIterations: opts.WarmupIterations, cancellationToken: ct);

        var throughputResult = await RunThroughputBenchmarkAsync(
            prompts[0],
            concurrentRequests: opts.ThroughputConcurrentRequests,
            duration: opts.ThroughputDuration,
            cancellationToken: ct);

        var costResult = await RunCostBenchmarkAsync(
            prompts, opts.CostModelName ?? _agent.Name, ct);

        // ── Build the three leaf EvalResults ─────────────────────────────────
        var latencyLeaf  = BuildLatencyLeaf(latencyResult,    opts);
        var throughputLeaf = BuildThroughputLeaf(throughputResult, opts);
        var costLeaf     = BuildCostLeaf(costResult,          opts);

        var leaves = new[] { latencyLeaf, throughputLeaf, costLeaf };
        var components = new[]
        {
            new EvalComponent(new SyntheticEval("perf_latency",   "Latency",   "performance", "1.0.0")),
            new EvalComponent(new SyntheticEval("perf_throughput", "Throughput","performance", "1.0.0")),
            new EvalComponent(new SyntheticEval("perf_cost",       "Cost",      "performance", "1.0.0")),
        };

        // CapByWorst: any high/critical-fail caps the composite.
        var (compositeScore, compositeSeverity) = CapByWorstAggregate(leaves, components);
        bool passed = compositeScore >= opts.CompositePassThreshold
            && leaves.All(l => l.Score.Label != "fail");
        string label = passed ? "pass"
            : leaves.Any(l => l.Score.Severity is "critical" or "high") ? "fail"
            : "warn";

        var dimensions = new Dictionary<string, double>
        {
            ["p99_ms"]    = latencyResult.P99Latency.TotalMilliseconds,
            ["rps"]       = throughputResult.RequestsPerSecond,
            ["cost_usd"]  = (double)(costResult.EstimatedCostUSD ?? 0m),
        };

        return new EvalResult(
            Metric: new("perf_benchmark", "Performance Benchmark", "performance", "1.0.0"),
            Score: new(compositeScore, null, label, passed, opts.CompositePassThreshold, compositeSeverity, null),
            Details: new(
                Dimensions: dimensions,
                Evidence: null,
                Recommendations: BuildRecommendations(latencyLeaf, throughputLeaf, costLeaf, opts),
                SubResults: leaves,
                AggregationStrategy: "CapByWorst"),
            Provenance: new("composite", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // ── Adapter helpers ───────────────────────────────────────────────────────

    private static List<string> ResolvePrompts(EvalInput input)
    {
        // Priority 1: Metadata["prompts"]
        if (input.Metadata?.TryGetValue("prompts", out var rawPrompts) == true && rawPrompts is not null)
        {
            if (rawPrompts is IEnumerable<string> enumerable)
            {
                var list = enumerable.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (list.Count > 0) return list;
            }
            if (rawPrompts is string csv && !string.IsNullOrWhiteSpace(csv))
            {
                var list = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (list.Count > 0) return list;
            }
        }

        // Priority 2: input.Query
        if (!string.IsNullOrWhiteSpace(input.Query))
            return [input.Query];

        return [];
    }

    private static EvalResult BuildLatencyLeaf(LatencyBenchmarkResult r, PerformanceBenchmarkEvaluateOptions opts)
    {
        var p99Ms = r.P99Latency.TotalMilliseconds;
        var threshold = opts.P99LatencyThresholdMs;

        // score: 1 − (p99 / threshold), clamped [0, 1]. Lower latency → higher score.
        var rawScore = threshold > 0 ? 1.0 - (p99Ms / threshold) : 1.0;
        var score = Math.Max(0.0, Math.Min(1.0, rawScore));

        var (label, severity) = score switch
        {
            >= 0.8 => ("pass", "none"),
            >= 0.5 => ("warn", "low"),
            >= 0.3 => ("warn", "medium"),
            >= 0.1 => ("fail", "high"),
            _      => ("fail", "critical"),
        };
        bool passed = label == "pass";

        return new EvalResult(
            Metric: new("perf_latency", "P99 Latency", "performance", "1.0.0"),
            Score: new(score, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["p50_ms"] = r.P50Latency.TotalMilliseconds,
                    ["p90_ms"] = r.P90Latency.TotalMilliseconds,
                    ["p99_ms"] = p99Ms,
                    ["mean_ms"] = r.MeanLatency.TotalMilliseconds,
                },
                Evidence: [new EvalEvidence("latency", r.AgentName, $"P99={p99Ms:F0}ms vs threshold={threshold:F0}ms")],
                Recommendations: passed ? null : [$"P99 latency {p99Ms:F0}ms exceeds threshold {threshold:F0}ms. Consider caching, reducing prompt length, or upgrading the model tier."],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("code", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult BuildThroughputLeaf(ThroughputBenchmarkResult r, PerformanceBenchmarkEvaluateOptions opts)
    {
        var rps = r.RequestsPerSecond;
        var minRps = opts.MinThroughputRps;

        // score: min(rps / minRps, 1.0). Higher throughput → higher score.
        var score = minRps > 0 ? Math.Min(rps / minRps, 1.0) : 1.0;

        var (label, severity) = score switch
        {
            >= 0.8 => ("pass", "none"),
            >= 0.5 => ("warn", "low"),
            >= 0.3 => ("warn", "medium"),
            >= 0.1 => ("fail", "high"),
            _      => ("fail", "critical"),
        };
        bool passed = label == "pass";

        return new EvalResult(
            Metric: new("perf_throughput", "Throughput", "performance", "1.0.0"),
            Score: new(score, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["rps"] = rps,
                    ["completed"] = r.CompletedRequests,
                    ["errors"] = r.ErrorCount,
                },
                Evidence: [new EvalEvidence("throughput", r.AgentName, $"{rps:F2} RPS vs minimum {minRps:F2} RPS")],
                Recommendations: passed ? null : [$"Throughput {rps:F2} RPS is below minimum {minRps:F2} RPS. Consider adding concurrency or reducing per-request cost."],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("code", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult BuildCostLeaf(CostBenchmarkResult r, PerformanceBenchmarkEvaluateOptions opts)
    {
        var costUsd = (double)(r.EstimatedCostUSD ?? 0m);
        var maxCost = opts.MaxCostUSD;

        double score;
        string label, severity;

        if (r.EstimatedCostUSD is null)
        {
            // No pricing data available — treat as pass with low severity.
            score = 1.0; label = "pass"; severity = "none";
        }
        else
        {
            // score: 1 − (cost / maxCost), clamped [0, 1]. Lower cost → higher score.
            var rawScore = maxCost > 0 ? 1.0 - (costUsd / maxCost) : 1.0;
            score = Math.Max(0.0, Math.Min(1.0, rawScore));
            (label, severity) = score switch
            {
                >= 0.8 => ("pass", "none"),
                >= 0.5 => ("warn", "low"),
                >= 0.3 => ("warn", "medium"),
                >= 0.1 => ("fail", "high"),
                _      => ("fail", "critical"),
            };
        }

        bool passed = label == "pass";

        return new EvalResult(
            Metric: new("perf_cost", "Cost", "performance", "1.0.0"),
            Score: new(score, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["total_tokens"] = r.TotalTokens,
                    ["input_tokens"] = r.TotalInputTokens,
                    ["output_tokens"] = r.TotalOutputTokens,
                    ["cost_usd"] = costUsd,
                },
                Evidence: [new EvalEvidence("cost", $"{r.AgentName} ({r.ModelName})",
                    r.EstimatedCostUSD.HasValue
                        ? $"${costUsd:F6} vs max ${maxCost:F6}"
                        : "Cost unknown — no pricing data for model")],
                Recommendations: passed ? null : [$"Estimated cost ${costUsd:F6} exceeds budget ${maxCost:F6}. Consider a smaller model or caching repeated prompts."],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("code", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string>? BuildRecommendations(
        EvalResult latency, EvalResult throughput, EvalResult cost,
        PerformanceBenchmarkEvaluateOptions _)
    {
        var recs = new List<string>();
        if (latency.Details.Recommendations is { Count: > 0 } lr) recs.AddRange(lr);
        if (throughput.Details.Recommendations is { Count: > 0 } tr) recs.AddRange(tr);
        if (cost.Details.Recommendations is { Count: > 0 } cr) recs.AddRange(cr);
        return recs.Count > 0 ? recs : null;
    }

    private static EvalResult MakeSkippedResult(string key, string name, string category, string reason)
        => new(
            Metric: new(key, name, category, "1.0.0"),
            Score: new(0.0, null, "skipped", false, null, "none", null),
            Details: new(null, null, new[] { reason }, null, null),
            Provenance: new("skipped", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    /// <summary>Minimal CapByWorst aggregation without taking a CoreEval dependency on the real class.</summary>
    private static (double Score, string Severity) CapByWorstAggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        double weightSum = 0, weightedSum = 0;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Score.Label == "skipped") continue;
            weightSum     += components[i].Weight;
            weightedSum   += results[i].Score.Value * components[i].Weight;
        }
        var rawScore = weightSum > 0 ? weightedSum / weightSum : 0;

        bool hasCrit = results.Any(r => r.Score.Label != "skipped" && r.Score.Severity == "critical" && !r.Score.Passed);
        bool hasHigh = results.Any(r => r.Score.Label != "skipped" && r.Score.Severity == "high"     && !r.Score.Passed);

        if (hasCrit) return (Math.Min(rawScore, 0.40), "critical");
        if (hasHigh) return (Math.Min(rawScore, 0.69), "high");

        // derive severity from the worst non-skipped leaf
        var severities = results
            .Where(r => r.Score.Label != "skipped")
            .Select(r => r.Score.Severity)
            .ToList();
        var worstSeverity = severities
            .OrderByDescending(s => s switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 })
            .FirstOrDefault() ?? "none";
        return (rawScore, worstSeverity);
    }

    /// <summary>Lightweight IEval stub used to satisfy EvalComponent constructor requirements.</summary>
    private sealed class SyntheticEval : IEval
    {
        public string Key      { get; }
        public string Name     { get; }
        public string Category { get; }
        public string Version  { get; }

        public SyntheticEval(string key, string name, string category, string version)
        {
            Key = key; Name = name; Category = category; Version = version;
        }

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException("SyntheticEval is a stub — call PerformanceBenchmark.EvaluateAsync instead.");
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
    /// Whether to print progress to console. Only consulted when <see cref="Logger"/> is null
    /// (the Console fallback); an injected logger receives progress at Information regardless.
    /// </summary>
    public bool Verbose { get; set; } = true;

    /// <summary>
    /// ARC-08: optional logger for progress (Information) and the no-pricing warning (Warning). When set,
    /// all benchmark output is routed here so hosts can capture/redirect it instead of it going straight
    /// to the process Console. When null, the original Console behaviour is preserved (progress to stdout
    /// only when <see cref="Verbose"/>; the no-pricing warning to stderr always).
    /// </summary>
    public IAgentEvalLogger? Logger { get; set; }

    /// <summary>
    /// Delay between benchmark iterations.
    /// </summary>
    public TimeSpan DelayBetweenIterations { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Threshold and budget options used by <see cref="PerformanceBenchmark.EvaluateAsync"/>.
    /// Defaults to <see cref="PerformanceBenchmarkEvaluateOptions.Default"/> when null.
    /// </summary>
    public PerformanceBenchmarkEvaluateOptions? EvaluateOptions { get; set; }
}

/// <summary>
/// Threshold and budget options that drive <see cref="PerformanceBenchmark.EvaluateAsync"/>.
/// All thresholds are tunable — create an instance and override only what you need.
/// </summary>
public sealed class PerformanceBenchmarkEvaluateOptions
{
    /// <summary>Default options (conservative thresholds suitable for most agents).</summary>
    public static PerformanceBenchmarkEvaluateOptions Default { get; } = new();

    // ── Latency ───────────────────────────────────────────────────────────────

    /// <summary>P99 latency budget in milliseconds. Score = 1 − (p99ms / threshold). Default: 5000 ms.</summary>
    public double P99LatencyThresholdMs { get; set; } = 5_000;

    /// <summary>Number of benchmark iterations per prompt when running via EvaluateAsync. Default: 3.</summary>
    public int LatencyIterationsPerPrompt { get; set; } = 3;

    /// <summary>Number of warmup iterations before the timed runs. Default: 1.</summary>
    public int WarmupIterations { get; set; } = 1;

    // ── Throughput ────────────────────────────────────────────────────────────

    /// <summary>Minimum acceptable RPS. Score = min(rps / minRps, 1.0). Default: 0.5.</summary>
    public double MinThroughputRps { get; set; } = 0.5;

    /// <summary>Number of concurrent requests for the throughput run. Default: 2.</summary>
    public int ThroughputConcurrentRequests { get; set; } = 2;

    /// <summary>Duration of the throughput measurement window. Default: 5 seconds.</summary>
    public TimeSpan ThroughputDuration { get; set; } = TimeSpan.FromSeconds(5);

    // ── Cost ──────────────────────────────────────────────────────────────────

    /// <summary>Maximum acceptable cost per evaluation in USD. Score = 1 − (cost / maxCost). Default: 0.10 USD.</summary>
    public double MaxCostUSD { get; set; } = 0.10;

    /// <summary>
    /// Model name used for cost estimation.
    /// When null, the agent's <c>Name</c> property is tried against the pricing table.
    /// </summary>
    public string? CostModelName { get; set; }

    // ── Composite ─────────────────────────────────────────────────────────────

    /// <summary>Minimum composite score required for a "pass" label. Default: 0.6.</summary>
    public double CompositePassThreshold { get; set; } = 0.6;
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
        $"  Mean: {MeanLatency.TotalMilliseconds:F0}ms | Min: {MinLatency.TotalMilliseconds:F0}ms | Max: {MaxLatency.TotalMilliseconds:F0}ms\n" +
        $"  P50: {P50Latency.TotalMilliseconds:F0}ms | P90: {P90Latency.TotalMilliseconds:F0}ms | P99: {P99Latency.TotalMilliseconds:F0}ms" +
        (MeanTimeToFirstToken.HasValue ? $"\n  Mean TTFT: {MeanTimeToFirstToken.Value.TotalMilliseconds:F0}ms" : "");
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
        $"  Duration: {Duration.TotalSeconds:F1}s | Concurrent: {ConcurrentRequests}\n" +
        $"  Completed: {CompletedRequests} | Errors: {ErrorCount}\n" +
        $"  RPS: {RequestsPerSecond:F2} | Mean Latency: {MeanLatency.TotalMilliseconds:F0}ms";
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
        $"  Avg per prompt: {AverageInputTokensPerPrompt:F0} in / {AverageOutputTokensPerPrompt:F0} out\n" +
        (EstimatedCostUSD.HasValue ? $"  Estimated Cost: ${EstimatedCostUSD:F6}" : "  Estimated Cost: Unknown pricing");
}
