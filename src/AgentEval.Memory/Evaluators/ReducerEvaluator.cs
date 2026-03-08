using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Evaluates information loss caused by chat context reduction/compression.
/// Plants facts early in conversation, adds noise to trigger the reducer,
/// then tests whether the agent still recalls the key facts.
/// </summary>
public class ReducerEvaluator : IReducerEvaluator
{
    private readonly IMemoryTestRunner _runner;
    private readonly ILogger<ReducerEvaluator> _logger;

    /// <summary>
    /// Default noise phrases for filling conversation history to trigger reducers.
    /// </summary>
    private static readonly string[] NoiseMessages =
    [
        "What time is it?",
        "Tell me about the weather.",
        "Nice talking to you!",
        "What's your opinion on cats vs dogs?",
        "Interesting perspective!",
        "Can you summarize that?",
        "What else should I know?",
        "Hmm, let me think about that.",
        "Good point! Tell me more.",
        "How about we change the subject?",
        "That reminds me of something funny.",
        "Sure, go ahead.",
        "Can you explain that in simpler terms?",
        "Sounds good to me!",
        "What's a fun hobby to try?",
        "I was just thinking the same thing.",
        "Any recommendations for dinner?",
        "What do you think about remote work?",
        "That's a great idea!",
        "Let's continue our conversation."
    ];

    public ReducerEvaluator(IMemoryTestRunner runner, ILogger<ReducerEvaluator> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReducerEvaluationResult> EvaluateAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryFact> facts,
        int noiseCount = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(facts);

        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting reducer fidelity evaluation with {FactCount} facts and {NoiseCount} noise turns",
            facts.Count, noiseCount);

        // Build scenario: establish facts → noise to trigger reducer → query each fact
        var steps = new List<MemoryStep>();

        // Phase 1: Plant key facts
        foreach (var fact in facts)
        {
            steps.Add(MemoryStep.Fact($"Please remember this important information: {fact.Content}"));
        }

        // Phase 2: Add noise to push facts out of short-term context
        for (int i = 0; i < noiseCount; i++)
        {
            steps.Add(MemoryStep.Noise(NoiseMessages[i % NoiseMessages.Length]));
        }

        // Phase 3: Create queries for each fact
        var queries = facts.Select(fact =>
            MemoryQuery.Create(
                $"Do you remember the information about: {fact.Content.Split(' ').Take(3).Aggregate((a, b) => $"{a} {b}")}?",
                fact)).ToList();

        var scenario = new MemoryTestScenario
        {
            Name = $"ReducerFidelity_{facts.Count}facts_{noiseCount}noise",
            Description = $"Tests {facts.Count} facts retention after {noiseCount} noise turns",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["EvaluationType"] = "ReducerFidelity",
                ["FactCount"] = facts.Count,
                ["NoiseCount"] = noiseCount
            }
        };

        try
        {
            var evalResult = await _runner.RunAsync(agent, scenario, cancellationToken);

            // Map query results back to facts
            var factResults = new List<ReducerFactResult>();
            for (int i = 0; i < facts.Count && i < evalResult.QueryResults.Count; i++)
            {
                var queryResult = evalResult.QueryResults[i];
                factResults.Add(new ReducerFactResult
                {
                    Fact = facts[i],
                    Retained = queryResult.Score >= 80,
                    Score = queryResult.Score,
                    Response = queryResult.Response
                });
            }

            stopwatch.Stop();

            var result = new ReducerEvaluationResult
            {
                ScenarioName = scenario.Name,
                FactResults = factResults,
                Duration = stopwatch.Elapsed,
                PreReductionMessageCount = steps.Count,
                PostReductionMessageCount = steps.Count // Actual post-reduction count depends on reducer
            };

            _logger.LogInformation("Reducer fidelity evaluation complete: {Retained}/{Total} facts retained ({Fidelity:F1}% fidelity), CriticalLoss={CriticalLoss}",
                result.RetainedCount, facts.Count, result.FidelityScore, result.HasCriticalLoss);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reducer fidelity evaluation");
            throw;
        }
    }
}
