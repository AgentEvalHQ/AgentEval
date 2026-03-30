// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Evaluates how far back an agent can recall facts through layers of conversational noise.
/// Tests recall at increasing depths to produce a degradation curve.
/// </summary>
public class ReachBackEvaluator : IReachBackEvaluator
{
    private readonly IMemoryTestRunner _runner;
    private readonly IMemoryJudge _judge;
    private readonly ILogger<ReachBackEvaluator> _logger;

    /// <summary>
    /// Default noise phrases used to generate distraction turns between the fact and query.
    /// </summary>
    private static readonly string[] DefaultNoiseMessages =
    [
        "What's the weather like today?",
        "Tell me a fun fact about space.",
        "What's your favorite programming language?",
        "Can you explain what machine learning is?",
        "What's the capital of Australia?",
        "Tell me a joke.",
        "What's 42 times 17?",
        "How do you make a good cup of coffee?",
        "What are the planets in the solar system?",
        "Tell me about the history of the internet.",
        "What's the tallest building in the world?",
        "How does photosynthesis work?",
        "What are some tips for better sleep?",
        "Can you recommend a good book?",
        "What's the difference between weather and climate?",
        "How does a computer processor work?",
        "What are the Olympic sports?",
        "Tell me about artificial intelligence.",
        "What's the speed of light?",
        "How do airplanes fly?"
    ];

    public ReachBackEvaluator(IMemoryTestRunner runner, IMemoryJudge judge, ILogger<ReachBackEvaluator> logger)
    {
        _runner = runner;
        _judge = judge;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReachBackResult> EvaluateAsync(
        IEvaluableAgent agent,
        MemoryFact fact,
        MemoryQuery query,
        IReadOnlyList<int> depths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(depths);

        var totalStopwatch = Stopwatch.StartNew();
        var depthResults = new List<DepthResult>();

        _logger.LogInformation("Starting reach-back evaluation for fact '{Fact}' at {DepthCount} depths: [{Depths}]",
            fact.Content, depths.Count, string.Join(", ", depths));

        foreach (var depth in depths.OrderBy(d => d))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reset agent state between depth tests to prevent contamination.
            // Without this, the agent accumulates conversation history from prior depths,
            // seeing the fact multiple times and invalidating the degradation curve.
            if (agent is ISessionResettableAgent resettable)
            {
                await resettable.ResetSessionAsync(cancellationToken);
            }

            var depthResult = await EvaluateAtDepthAsync(agent, fact, query, depth, cancellationToken);
            depthResults.Add(depthResult);

            _logger.LogDebug("Depth {Depth}: Score={Score:F1}%, Recalled={Recalled}",
                depth, depthResult.Score, depthResult.Recalled);
        }

        totalStopwatch.Stop();

        var result = new ReachBackResult
        {
            Fact = fact,
            DepthResults = depthResults,
            Duration = totalStopwatch.Elapsed
        };

        _logger.LogInformation("Reach-back evaluation complete: MaxReliableDepth={MaxDepth}, FailurePoint={FailurePoint}, OverallScore={Score:F1}%",
            result.MaxReliableDepth, result.FailurePoint?.ToString() ?? "none", result.OverallScore);

        return result;
    }

    /// <summary>
    /// Evaluates recall at a single noise depth by building a scenario,
    /// running it through the test runner, and extracting the score.
    /// </summary>
    private async Task<DepthResult> EvaluateAtDepthAsync(
        IEvaluableAgent agent,
        MemoryFact fact,
        MemoryQuery query,
        int depth,
        CancellationToken cancellationToken)
    {
        var depthStopwatch = Stopwatch.StartNew();

        // Build scenario: fact → N noise turns → query
        var steps = new List<MemoryStep>
        {
            MemoryStep.Fact($"Please remember this: {fact.Content}")
        };

        for (int i = 0; i < depth; i++)
        {
            var noiseMessage = DefaultNoiseMessages[i % DefaultNoiseMessages.Length];
            steps.Add(MemoryStep.Noise(noiseMessage));
        }

        var scenario = new MemoryTestScenario
        {
            Name = $"ReachBack_Depth_{depth}",
            Description = $"Reach-back test at depth {depth}",
            Steps = steps,
            Queries = [query],
            Metadata = new Dictionary<string, object>
            {
                ["ReachBackDepth"] = depth,
                ["FactContent"] = fact.Content
            }
        };

        try
        {
            var evalResult = await _runner.RunAsync(agent, scenario, cancellationToken);
            depthStopwatch.Stop();

            var firstQueryResult = evalResult.QueryResults.FirstOrDefault();

            return new DepthResult
            {
                Depth = depth,
                Score = firstQueryResult?.Score ?? 0,
                Response = firstQueryResult?.Response ?? string.Empty,
                Duration = depthStopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during reach-back evaluation at depth {Depth}", depth);
            depthStopwatch.Stop();

            return new DepthResult
            {
                Depth = depth,
                Score = 0,
                Response = $"Error: {ex.Message}",
                Duration = depthStopwatch.Elapsed
            };
        }
    }
}
