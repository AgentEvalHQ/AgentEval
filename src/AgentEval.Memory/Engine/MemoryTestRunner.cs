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
    /// </summary>
    private async Task ExecuteSetupStepsAsync(
        IEvaluableAgent agent, 
        IReadOnlyList<MemoryStep> steps, 
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing {StepCount} setup steps", steps.Count);
        
        foreach (var step in steps)
        {
            try
            {
                var response = await agent.InvokeAsync(step.Content, cancellationToken);
                
                // Optional: Validate expected response if specified
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
        
        // Calculate overall score as weighted average
        // Penalize for forbidden facts found
        var baseScore = queryResults.Count > 0 ? queryResults.Average(r => r.Score) : 0;
        var forbiddenPenalty = allForbiddenFound.Count * 10; // -10 points per forbidden fact
        var overallScore = Math.Max(0, baseScore - forbiddenPenalty);
        
        // Estimate cost (rough approximation - should be configurable)
        var estimatedCost = totalTokens * 0.00001m; // $0.01 per 1K tokens (approximate)
        
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