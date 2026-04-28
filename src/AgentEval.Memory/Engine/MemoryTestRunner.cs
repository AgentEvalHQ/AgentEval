// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;

namespace AgentEval.Memory.Engine;

/// <summary>
/// Core orchestration engine for memory evaluation scenarios.
/// Executes scenario steps, runs queries, and coordinates with MemoryJudge for scoring.
/// </summary>
public class MemoryTestRunner : IMemoryTestRunner
{
    private readonly IMemoryJudge _memoryJudge;
    private readonly ILogger<MemoryTestRunner> _logger;

    public MemoryTestRunner(IMemoryJudge memoryJudge, ILogger<MemoryTestRunner> logger)
    {
        _memoryJudge = memoryJudge;
        _logger = logger;
    }

    /// <summary>
    /// Creates a minimal <see cref="MemoryTestRunner"/> for use without a DI container.
    /// Suitable for scripting and simple test scenarios.
    /// </summary>
    /// <param name="chatClient">Chat client used by the memory judge for LLM-based evaluation.</param>
    public static MemoryTestRunner Create(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        return new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
    }

    /// <summary>
    /// Runs a complete memory evaluation scenario against an agent.
    /// </summary>
    /// <param name="agent">The agent to evaluate</param>
    /// <param name="scenario">The memory test scenario to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive memory evaluation results</returns>
    public async Task<MemoryEvaluationResult> RunAsync(
        IEvaluableAgent agent, 
        MemoryTestScenario scenario, 
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(scenario);
        
        var stopwatch = Stopwatch.StartNew();
        var totalTokens = 0;
        
        _logger.LogInformation("Starting memory evaluation scenario: {ScenarioName}", scenario.Name);

        try
        {
            // Phase 1: Build or use pre-built text blob from setup steps
            var contextBlob = scenario.ContextTextBlob;
            if (contextBlob == null && scenario.Steps.Count > 0)
            {
                // Check for cross-session scenarios that need individual InvokeAsync calls
                var hasSessionResets = scenario.Steps.Any(s => s.Content.Contains("[SESSION_RESET_POINT]"));
                if (hasSessionResets)
                {
                    // Cross-session: must use individual calls for session reset support
                    await ExecuteSetupStepsWithCallsAsync(agent, scenario.Steps, cancellationToken);
                }
                else
                {
                    // Standard path: build text blob from steps (no LLM calls)
                    contextBlob = BuildTextBlobFromSteps(scenario.Steps);
                    _logger.LogDebug("Built text blob from {StepCount} setup steps ({CharCount} chars)", 
                        scenario.Steps.Count, contextBlob.Length);
                }
            }
            
            // Phase 2: Run verification queries (prepending text blob if available)
            var queryResults = new List<MemoryQueryResult>();
            
            foreach (var query in scenario.Queries)
            {
                var queryResult = await EvaluateQueryAsync(agent, query, contextBlob, cancellationToken);
                queryResults.Add(queryResult);
                totalTokens += queryResult.TokensUsed;
                
                _logger.LogDebug("Query '{Question}' scored {Score:F1}%", 
                    query.Question, queryResult.Score);
            }
            
            stopwatch.Stop();
            
            // Phase 3: Aggregate results
            var result = AggregateResults(scenario, queryResults, stopwatch.Elapsed, totalTokens);
            
            _logger.LogInformation("Memory evaluation completed: {ScenarioName} - Overall Score: {Score:F1}% ({Passed}/{Total} queries passed)",
                scenario.Name, result.OverallScore, result.PassedQueries, result.TotalQueries);
                
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during memory evaluation scenario: {ScenarioName}", scenario.Name);
            throw;
        }
    }

    /// <summary>
    /// Implementation of interface method. Delegates to RunAsync.
    /// </summary>
    public Task<MemoryEvaluationResult> RunMemoryTestAsync(
        IEvaluableAgent agent, 
        MemoryTestScenario scenario, 
        CancellationToken cancellationToken = default)
    {
        return RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Runs multiple memory queries against an agent without a full scenario structure.
    /// </summary>
    public async Task<MemoryEvaluationResult> RunMemoryQueriesAsync(
        IEvaluableAgent agent, 
        IEnumerable<MemoryQuery> queries, 
        string scenarioName,
        CancellationToken cancellationToken = default)
    {
        // Create a minimal scenario from the queries
        var queryList = queries.ToList();
        var scenario = new MemoryTestScenario
        {
            Name = scenarioName,
            Description = $"Memory test with {queryList.Count} queries",
            Steps = [], // No setup steps
            Queries = queryList,
            Metadata = new Dictionary<string, object>
            {
                ["QueryCount"] = queryList.Count,
                ["DirectQueryExecution"] = true
            }
        };

        return await RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Builds a text blob from scenario steps (facts + noise) formatted as conversation history.
    /// This matches the LongMemEval text-blob approach — all context is prepended to each query
    /// as a single block of text. No LLM calls needed for setup.
    /// </summary>
    internal static string BuildTextBlobFromSteps(IReadOnlyList<MemoryStep> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Below is a conversation history between you and a user. Use it to answer the question that follows.");
        sb.AppendLine();
        sb.AppendLine("Conversation History:");

        foreach (var step in steps)
        {
            var response = step.Type switch
            {
                MemoryStepType.Fact => "Got it, I'll remember that.",
                MemoryStepType.Noise => "Interesting, thanks for sharing.",
                _ => "I understand."
            };

            sb.AppendLine($"user: {step.Content}");
            sb.AppendLine($"assistant: {response}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Fallback: executes setup steps via individual InvokeAsync calls.
    /// Only used for cross-session scenarios that contain [SESSION_RESET_POINT] markers,
    /// which require actual session resets between steps.
    /// </summary>
    private async Task ExecuteSetupStepsWithCallsAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0) return;

        _logger.LogDebug("Executing {StepCount} setup steps via individual calls (cross-session mode)", steps.Count);

        foreach (var step in steps)
        {
            if (step.Content.Contains("[SESSION_RESET_POINT]"))
            {
                if (agent is ISessionResettableAgent resettable)
                {
                    await resettable.ResetSessionAsync(cancellationToken);
                    _logger.LogDebug("Session reset executed at [SESSION_RESET_POINT]");
                }
                else
                {
                    _logger.LogWarning(
                        "Scenario requires session reset but agent does not implement ISessionResettableAgent. " +
                        "Cross-session evaluation results may be unreliable.");
                }
                continue;
            }

            try
            {
                var response = await agent.InvokeAsync(step.Content, cancellationToken);

                if (!string.IsNullOrEmpty(step.ExpectedResponse))
                {
                    if (!response.Text.Contains(step.ExpectedResponse, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Step response validation failed. Expected: '{Expected}', Got: '{Actual}'",
                            step.ExpectedResponse, response.Text);
                    }
                }

                _logger.LogTrace("Setup step executed: {StepType} - {Content}", step.Type, step.Content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing setup step: {Content}", step.Content);
                throw;
            }
        }
    }

    /// <summary>
    /// Evaluates a single memory query against the agent.
    /// When contextBlob is provided, it is prepended to the query (text-blob injection).
    /// </summary>
    private async Task<MemoryQueryResult> EvaluateQueryAsync(
        IEvaluableAgent agent, 
        MemoryQuery query,
        string? contextBlob,
        CancellationToken cancellationToken)
    {
        var queryStopwatch = Stopwatch.StartNew();
        
        try
        {
            // Prepend text blob to query if available (text-blob injection, same as LongMemEval)
            var prompt = contextBlob != null
                ? $"{contextBlob}\nQuestion: {query.Question}\nAnswer:"
                : query.Question;

            // Get agent's response to the memory query
            var response = await agent.InvokeAsync(prompt, cancellationToken);
            
            // Use MemoryJudge to evaluate the response
            var judgmentResult = await _memoryJudge.JudgeAsync(response.Text, query, cancellationToken);
            
            queryStopwatch.Stop();
            
            return new MemoryQueryResult
            {
                Query = query,
                Response = response.Text,
                Score = judgmentResult.Score,
                FoundFacts = judgmentResult.FoundFacts,
                MissingFacts = judgmentResult.MissingFacts,
                ForbiddenFound = judgmentResult.ForbiddenFound,
                Explanation = judgmentResult.Explanation,
                Duration = queryStopwatch.Elapsed,
                TokensUsed = judgmentResult.TokensUsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating query: {Question}", query.Question);
            throw;
        }
    }

    /// <summary>
    /// Aggregates individual query results into a comprehensive evaluation result.
    /// </summary>
    private static MemoryEvaluationResult AggregateResults(
        MemoryTestScenario scenario, 
        IReadOnlyList<MemoryQueryResult> queryResults, 
        TimeSpan totalDuration, 
        int totalTokens)
    {
        var allFoundFacts = queryResults.SelectMany(r => r.FoundFacts).Distinct().ToList();
        var allMissingFacts = queryResults.SelectMany(r => r.MissingFacts).Distinct().ToList();
        var allForbiddenFound = queryResults.SelectMany(r => r.ForbiddenFound).Distinct().ToList();
        
        // Calculate overall score as average of per-query scores.
        // Forbidden fact penalties are already applied by the LLM judge at the per-query level
        // (the judge prompt instructs "Subtract 10-20 points per forbidden fact found").
        // No additional penalty here — that would double-penalize.
        var overallScore = queryResults.Count > 0 ? queryResults.Average(r => r.Score) : 0;
        
        // Cost estimation: $0.003 per 1K tokens (approximate, varies by model/provider).
        // Users should compute exact costs from TokensUsed with their own pricing.
        var estimatedCost = totalTokens * 0.003m / 1000m;
        
        return new MemoryEvaluationResult
        {
            OverallScore = overallScore,
            QueryResults = queryResults,
            FoundFacts = allFoundFacts,
            MissingFacts = allMissingFacts,
            ForbiddenFound = allForbiddenFound,
            Duration = totalDuration,
            TokensUsed = totalTokens,
            EstimatedCost = estimatedCost,
            ScenarioName = scenario.Name
        };
    }
}