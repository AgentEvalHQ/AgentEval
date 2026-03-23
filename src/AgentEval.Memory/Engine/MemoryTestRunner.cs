using AgentEval.Core;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
            // Phase 1: Execute setup steps to establish facts
            await ExecuteSetupStepsAsync(agent, scenario.Steps, cancellationToken);
            
            // Phase 2: Run verification queries
            var queryResults = new List<MemoryQueryResult>();
            
            foreach (var query in scenario.Queries)
            {
                var queryResult = await EvaluateQueryAsync(agent, query, cancellationToken);
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
    /// Executes the setup steps to establish facts in the agent's memory.
    /// When the agent supports history injection (IHistoryInjectableAgent), injects
    /// facts and noise as synthetic conversation history — ZERO LLM calls.
    /// Falls back to individual LLM calls only when history injection is not supported
    /// or when session reset markers are present (cross-session scenarios).
    /// </summary>
    private async Task ExecuteSetupStepsAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0) return;

        _logger.LogDebug("Executing {StepCount} setup steps", steps.Count);

        // Check if any steps require session resets — if so, fall back to individual calls
        var hasSessionResets = steps.Any(s => s.Content.Contains("[SESSION_RESET_POINT]"));

        // Use efficient history injection when possible
        if (!hasSessionResets && agent is IHistoryInjectableAgent injectable)
        {
            var history = BuildSyntheticHistory(steps);
            if (history.Count > 0)
            {
                injectable.InjectConversationHistory(history);
                _logger.LogDebug("Injected {TurnCount} turns as conversation history (0 LLM calls)", history.Count);
                return;
            }
        }

        // Fallback: individual LLM calls (needed for cross-session scenarios with resets)
        _logger.LogDebug("Using individual LLM calls for setup steps (session resets detected or no history injection)");

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
    /// Converts scenario steps into synthetic conversation history pairs (user, assistant).
    /// Facts become user messages, noise becomes user messages, and all get synthetic assistant responses.
    /// </summary>
    private static IReadOnlyList<(string UserMessage, string AssistantResponse)> BuildSyntheticHistory(
        IReadOnlyList<MemoryStep> steps)
    {
        var history = new List<(string UserMessage, string AssistantResponse)>();

        foreach (var step in steps)
        {
            var response = step.Type switch
            {
                MemoryStepType.Fact => "Got it, I'll remember that.",
                MemoryStepType.Noise => "Interesting, thanks for sharing.",
                _ => "I understand."
            };

            history.Add((step.Content, response));
        }

        return history;
    }

    /// <summary>
    /// Evaluates a single memory query against the agent.
    /// </summary>
    private async Task<MemoryQueryResult> EvaluateQueryAsync(
        IEvaluableAgent agent, 
        MemoryQuery query, 
        CancellationToken cancellationToken)
    {
        var queryStopwatch = Stopwatch.StartNew();
        
        try
        {
            // Get agent's response to the memory query
            var response = await agent.InvokeAsync(query.Question, cancellationToken);
            
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