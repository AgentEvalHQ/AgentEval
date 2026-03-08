using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentEval.Memory.Temporal;

/// <summary>
/// Specialized runner for temporal memory scenarios that handles time-aware evaluation.
/// Extends the basic MemoryTestRunner with temporal-specific capabilities.
/// </summary>
public class TemporalMemoryRunner : ITemporalMemoryRunner
{
    private readonly IMemoryTestRunner _baseRunner;
    private readonly ILogger<TemporalMemoryRunner> _logger;

    public TemporalMemoryRunner(IMemoryTestRunner baseRunner, ILogger<TemporalMemoryRunner> logger)
    {
        _baseRunner = baseRunner;
        _logger = logger;
    }

    /// <summary>
    /// Runs a temporal memory scenario with time-aware processing.
    /// Handles temporal markers and time-travel queries appropriately.
    /// </summary>
    /// <param name="agent">The agent to evaluate</param>
    /// <param name="scenario">Temporal memory scenario</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result with temporal metadata</returns>
    public async Task<MemoryEvaluationResult> RunTemporalScenarioAsync(
        IEvaluableAgent agent,
        MemoryTestScenario scenario,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting temporal memory evaluation: {ScenarioName}", scenario.Name);
        
        // Check if scenario has temporal characteristics
        var isTemporalScenario = scenario.Steps.Any(s => s.Timestamp.HasValue) ||
                                scenario.Queries.Any(q => q.QueryTime.HasValue) ||
                                (scenario.Metadata?.ContainsKey("TemporalQuery") == true);
        
        if (!isTemporalScenario)
        {
            _logger.LogWarning("Scenario '{ScenarioName}' doesn't appear to be temporal - delegating to base runner", scenario.Name);
            return await _baseRunner.RunAsync(agent, scenario, cancellationToken);
        }
        
        // Process scenario with temporal awareness
        var result = await ProcessTemporalScenario(agent, scenario, cancellationToken);
        
        // Add temporal metadata to results
        var enhancedResult = EnhanceWithTemporalMetadata(result, scenario);
        
        _logger.LogInformation("Temporal memory evaluation completed: {ScenarioName} - Score: {Score:F1}%",
            scenario.Name, enhancedResult.OverallScore);
            
        return enhancedResult;
    }

    /// <summary>
    /// Processes a temporal scenario with special handling for time-based steps and queries.
    /// </summary>
    private async Task<MemoryEvaluationResult> ProcessTemporalScenario(
        IEvaluableAgent agent,
        MemoryTestScenario scenario,
        CancellationToken cancellationToken)
    {
        // For temporal scenarios, we may need to simulate the passage of time
        // or provide temporal context to the agent
        
        // Check if we need to provide temporal context
        var needsTemporalContext = scenario.Steps.Any(s => s.Timestamp.HasValue);
        
        if (needsTemporalContext)
        {
            // Create an enhanced scenario with temporal context
            var enhancedScenario = AddTemporalContext(scenario);
            return await _baseRunner.RunAsync(agent, enhancedScenario, cancellationToken);
        }
        
        // Otherwise, run normally
        return await _baseRunner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Adds temporal context to scenario steps where needed.
    /// </summary>
    private MemoryTestScenario AddTemporalContext(MemoryTestScenario scenario)
    {
        var enhancedSteps = new List<MemoryStep>();
        
        // Add initial temporal context
        if (scenario.Steps.Any(s => s.Timestamp.HasValue))
        {
            var timeRange = GetTimeRange(scenario.Steps);
            enhancedSteps.Add(MemoryStep.System(
                $"TEMPORAL CONTEXT: This conversation spans from {timeRange.start:yyyy-MM-dd HH:mm} to {timeRange.end:yyyy-MM-dd HH:mm}. " +
                $"Pay attention to when things happened, as you may be asked about specific time periods."
            ));
        }
        
        // Process each step, adding timestamp context where available
        DateTimeOffset? lastTimestamp = null;
        
        foreach (var step in scenario.Steps)
        {
            if (step.Timestamp.HasValue)
            {
                // Add time transition marker if there's a significant gap
                if (lastTimestamp.HasValue && step.Timestamp.Value - lastTimestamp.Value > TimeSpan.FromHours(1))
                {
                    enhancedSteps.Add(MemoryStep.System(
                        $"[TIME JUMP: Now it's {step.Timestamp:yyyy-MM-dd HH:mm}]"
                    ));
                }
                
                lastTimestamp = step.Timestamp;
            }
            
            enhancedSteps.Add(step);
        }
        
        return new MemoryTestScenario
        {
            Name = scenario.Name,
            Description = scenario.Description,
            Steps = enhancedSteps,
            Queries = scenario.Queries,
            Timeout = scenario.Timeout,
            Metadata = scenario.Metadata
        };
    }

    /// <summary>
    /// Enhances evaluation results with temporal-specific metadata.
    /// </summary>
    private MemoryEvaluationResult EnhanceWithTemporalMetadata(
        MemoryEvaluationResult result,
        MemoryTestScenario scenario)
    {
        var temporalMetadata = new Dictionary<string, object>(result.Metadata ?? new Dictionary<string, object>())
        {
            ["TemporalEvaluation"] = true
        };
        
        // Add time range information
        var stepTimeRange = GetTimeRange(scenario.Steps);
        if (stepTimeRange.start != default && stepTimeRange.end != default)
        {
            temporalMetadata["TimeRange"] = new { stepTimeRange.start, stepTimeRange.end };
            temporalMetadata["TimeSpan"] = stepTimeRange.end - stepTimeRange.start;
        }
        
        // Add temporal query information
        var temporalQueries = scenario.Queries.Where(q => q.QueryTime.HasValue).ToArray();
        if (temporalQueries.Length > 0)
        {
            temporalMetadata["TemporalQueryCount"] = temporalQueries.Length;
            temporalMetadata["QueryTimes"] = temporalQueries.Select(q => q.QueryTime!.Value).ToArray();
        }
        
        // Calculate temporal-specific scores
        if (temporalQueries.Length > 0)
        {
            var temporalQueryResults = result.QueryResults
                .Where(r => r.Query.QueryTime.HasValue)
                .ToArray();
                
            var temporalScore = temporalQueryResults.Length > 0
                ? temporalQueryResults.Average(r => r.Score)
                : result.OverallScore;
                
            temporalMetadata["TemporalScore"] = temporalScore;
            temporalMetadata["TemporalAccuracy"] = temporalQueryResults.Count(r => r.Passed) / (double)temporalQueryResults.Length * 100;
        }
        
        return new MemoryEvaluationResult
        {
            OverallScore = result.OverallScore,
            QueryResults = result.QueryResults,
            FoundFacts = result.FoundFacts,
            MissingFacts = result.MissingFacts,
            ForbiddenFound = result.ForbiddenFound,
            Duration = result.Duration,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            ScenarioName = result.ScenarioName,
            Timestamp = result.Timestamp,
            Metadata = temporalMetadata
        };
    }

    /// <summary>
    /// Gets the time range covered by scenario steps.
    /// </summary>
    private static (DateTimeOffset start, DateTimeOffset end) GetTimeRange(IReadOnlyList<MemoryStep> steps)
    {
        var timestampedSteps = steps.Where(s => s.Timestamp.HasValue).ToArray();
        
        if (timestampedSteps.Length == 0)
            return (default, default);
            
        var timestamps = timestampedSteps.Select(s => s.Timestamp!.Value).ToArray();
        return (timestamps.Min(), timestamps.Max());
    }

    /// <inheritdoc />
    public async Task<MemoryEvaluationResult> TestTimeTravelQueriesAsync(
        IEvaluableAgent agent,
        IEnumerable<string> conversationHistory,
        IEnumerable<MemoryQuery> temporalQueries,
        CancellationToken cancellationToken = default)
    {
        var history = conversationHistory.ToArray();
        var queries = temporalQueries.ToArray();

        _logger.LogInformation("Testing time-travel queries: {QueryCount} queries over {HistoryCount} history items",
            queries.Length, history.Length);

        // Build a scenario from the conversation history and temporal queries
        var steps = history.Select(h => MemoryStep.Conversation(h)).ToList();

        var scenario = new MemoryTestScenario
        {
            Name = "Time Travel Query Test",
            Description = $"Tests {queries.Length} temporal queries over conversation history",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["TemporalQuery"] = true,
                ["HistoryLength"] = history.Length
            }
        };

        return await RunTemporalScenarioAsync(agent, scenario, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryEvaluationResult> TestCausalReasoningAsync(
        IEvaluableAgent agent,
        IEnumerable<MemoryFact> causalChain,
        IEnumerable<string> reasoningQueries,
        CancellationToken cancellationToken = default)
    {
        var chain = causalChain.ToArray();
        var queryStrings = reasoningQueries.ToArray();

        _logger.LogInformation("Testing causal reasoning: {ChainLength} events, {QueryCount} queries",
            chain.Length, queryStrings.Length);

        var steps = chain.Select(f =>
            MemoryStep.Temporal(f.Content, f.Timestamp ?? DateTimeOffset.UtcNow)).ToList();

        var queries = queryStrings.Select(q =>
            MemoryQuery.Create(q, chain)).ToArray();

        var scenario = new MemoryTestScenario
        {
            Name = "Causal Reasoning Test",
            Description = $"Tests causal reasoning across {chain.Length} events",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["CausalReasoning"] = true,
                ["ChainLength"] = chain.Length
            }
        };

        return await RunTemporalScenarioAsync(agent, scenario, cancellationToken);
    }
}