using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Runs comprehensive memory benchmark suites against agents.
/// Orchestrates scenario execution across multiple categories and produces holistic scores.
/// </summary>
public class MemoryBenchmarkRunner : IMemoryBenchmarkRunner
{
    private readonly IMemoryTestRunner _runner;
    private readonly IMemoryJudge _judge;
    private readonly IReachBackEvaluator _reachBackEvaluator;
    private readonly IReducerEvaluator _reducerEvaluator;
    private readonly ICrossSessionEvaluator _crossSessionEvaluator;
    private readonly IMemoryScenarios _memoryScenarios;
    private readonly IChattyConversationScenarios _chattyScenarios;
    private readonly ITemporalMemoryScenarios _temporalScenarios;
    private readonly ILogger<MemoryBenchmarkRunner> _logger;

    public MemoryBenchmarkRunner(
        IMemoryTestRunner runner,
        IMemoryJudge judge,
        IReachBackEvaluator reachBackEvaluator,
        IReducerEvaluator reducerEvaluator,
        ICrossSessionEvaluator crossSessionEvaluator,
        IMemoryScenarios memoryScenarios,
        IChattyConversationScenarios chattyScenarios,
        ITemporalMemoryScenarios temporalScenarios,
        ILogger<MemoryBenchmarkRunner> logger)
    {
        _runner = runner;
        _judge = judge;
        _reachBackEvaluator = reachBackEvaluator;
        _reducerEvaluator = reducerEvaluator;
        _crossSessionEvaluator = crossSessionEvaluator;
        _memoryScenarios = memoryScenarios;
        _chattyScenarios = chattyScenarios;
        _temporalScenarios = temporalScenarios;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(benchmark);

        var totalStopwatch = Stopwatch.StartNew();
        var categoryResults = new List<BenchmarkCategoryResult>();

        _logger.LogInformation("Starting memory benchmark: {BenchmarkName} ({CategoryCount} categories)",
            benchmark.Name, benchmark.Categories.Count);

        foreach (var category in benchmark.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reset agent state between categories to prevent cross-contamination.
            // Without this, facts planted in BasicRetention leak into subsequent categories.
            if (agent is ISessionResettableAgent resettable)
            {
                await resettable.ResetSessionAsync(cancellationToken);
            }

            _logger.LogDebug("Running benchmark category: {CategoryName}", category.Name);

            var catResult = await RunCategoryAsync(agent, category, cancellationToken);
            categoryResults.Add(catResult);

            _logger.LogDebug("Category '{CategoryName}': Score={Score:F1}%, Skipped={Skipped}",
                category.Name, catResult.Score, catResult.Skipped);
        }

        totalStopwatch.Stop();

        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = benchmark.Name,
            CategoryResults = categoryResults,
            Duration = totalStopwatch.Elapsed
        };

        _logger.LogInformation("Memory benchmark complete: {BenchmarkName} — Overall={Score:F1}% Grade={Grade} ({Stars} stars)",
            benchmark.Name, result.OverallScore, result.Grade, result.Stars);

        if (result.WeakCategories.Count > 0)
        {
            _logger.LogInformation("Weak categories: {WeakCategories}", string.Join(", ", result.WeakCategories));
        }

        return result;
    }

    /// <summary>
    /// Runs a single benchmark category and returns the result.
    /// </summary>
    private async Task<BenchmarkCategoryResult> RunCategoryAsync(
        IEvaluableAgent agent,
        MemoryBenchmarkCategory category,
        CancellationToken cancellationToken)
    {
        var catStopwatch = Stopwatch.StartNew();

        try
        {
            var score = category.ScenarioType switch
            {
                BenchmarkScenarioType.BasicRetention => await RunBasicRetentionAsync(agent, cancellationToken),
                BenchmarkScenarioType.TemporalReasoning => await RunTemporalReasoningAsync(agent, cancellationToken),
                BenchmarkScenarioType.NoiseResilience => await RunNoiseResilienceAsync(agent, cancellationToken),
                BenchmarkScenarioType.ReachBackDepth => await RunReachBackAsync(agent, cancellationToken),
                BenchmarkScenarioType.FactUpdateHandling => await RunFactUpdateAsync(agent, cancellationToken),
                BenchmarkScenarioType.MultiTopic => await RunMultiTopicAsync(agent, cancellationToken),
                BenchmarkScenarioType.CrossSession => await RunCrossSessionAsync(agent, cancellationToken),
                BenchmarkScenarioType.ReducerFidelity => await RunReducerFidelityAsync(agent, cancellationToken),
                _ => (Score: 0.0, Skipped: true, SkipReason: $"Unknown scenario type: {category.ScenarioType}")
            };

            catStopwatch.Stop();

            return new BenchmarkCategoryResult
            {
                CategoryName = category.Name,
                Score = score.Score,
                Weight = category.Weight,
                ScenarioType = category.ScenarioType,
                Duration = catStopwatch.Elapsed,
                Skipped = score.Skipped,
                SkipReason = score.SkipReason
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error running benchmark category: {CategoryName}", category.Name);
            catStopwatch.Stop();

            return new BenchmarkCategoryResult
            {
                CategoryName = category.Name,
                Score = 0,
                Weight = category.Weight,
                ScenarioType = category.ScenarioType,
                Duration = catStopwatch.Elapsed,
                Skipped = true,
                SkipReason = $"Error: {ex.Message}"
            };
        }
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunBasicRetentionAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        MemoryFact[] facts =
        [
            MemoryFact.Create("My name is José"),
            MemoryFact.Create("I live in Copenhagen"),
            MemoryFact.Create("I'm allergic to peanuts"),
            MemoryFact.Create("My meeting is at 3pm tomorrow"),
            MemoryFact.Create("I prefer email over Slack")
        ];

        MemoryQuery[] queries =
        [
            MemoryQuery.Create("What do you remember about me?", facts),
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("Where do I live?", facts[1]),
            MemoryQuery.Create("Do I have any food allergies?", facts[2])
        ];

        var scenario = _memoryScenarios.CreateBasicMemoryTest(facts, queries);
        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunTemporalReasoningAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        var scenario = _temporalScenarios.CreateSequenceMemoryTest(
            [
                MemoryFact.Create("Started learning Python", DateTimeOffset.UtcNow.AddMonths(-12)),
                MemoryFact.Create("Got first junior developer job", DateTimeOffset.UtcNow.AddMonths(-8)),
                MemoryFact.Create("Learned C# at work", DateTimeOffset.UtcNow.AddMonths(-6)),
                MemoryFact.Create("Promoted to mid-level developer", DateTimeOffset.UtcNow.AddMonths(-2))
            ]);

        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunNoiseResilienceAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        MemoryFact[] facts =
        [
            MemoryFact.Create("I'm allergic to peanuts", "allergy", 100),
            MemoryFact.Create("My meeting is at 3pm", "schedule", 80)
        ];

        var scenario = _chattyScenarios.CreateBuriedFactsScenario(facts);
        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunReachBackAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        var fact = MemoryFact.Create("I'm allergic to peanuts", "allergy", 100);
        var query = MemoryQuery.Create("What food allergies should you know about?", fact);

        var result = await _reachBackEvaluator.EvaluateAsync(
            agent, fact, query,
            [5, 10, 25],
            ct);

        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunFactUpdateAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        var scenario = _memoryScenarios.CreateMemoryUpdateTest(
            [
                MemoryFact.Create("My favorite color is blue"),
                MemoryFact.Create("I drive a Honda Civic")
            ],
            [
                MemoryFact.Create("Actually, my favorite color is now green"),
                MemoryFact.Create("I sold the Honda and bought a Tesla")
            ]);

        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunMultiTopicAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        MemoryFact[] facts =
        [
            MemoryFact.Create("My dog's name is Max", "pets"),
            MemoryFact.Create("I work at Contoso", "work"),
            MemoryFact.Create("My birthday is March 15th", "personal"),
            MemoryFact.Create("I speak Danish and English", "languages"),
            MemoryFact.Create("I take vitamin D supplements", "health")
        ];

        MemoryQuery[] queries =
        [
            MemoryQuery.Create("What do you remember about me?", facts),
            MemoryQuery.Create("Do you know my pet's name?", facts[0]),
            MemoryQuery.Create("Where do I work?", facts[1]),
            MemoryQuery.Create("When is my birthday?", facts[2])
        ];

        var scenario = _memoryScenarios.CreateBasicMemoryTest(facts, queries);
        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunCrossSessionAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        if (agent is not ISessionResettableAgent)
        {
            return (0, true, "Agent does not implement ISessionResettableAgent");
        }

        var facts = new List<MemoryFact>
        {
            MemoryFact.Create("My name is José"),
            MemoryFact.Create("I live in Copenhagen"),
            MemoryFact.Create("I'm allergic to peanuts")
        };

        var result = await _crossSessionEvaluator.EvaluateAsync(agent, facts, 0.8, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunReducerFidelityAsync(
        IEvaluableAgent agent, CancellationToken ct)
    {
        var facts = new List<MemoryFact>
        {
            MemoryFact.Create("I'm allergic to peanuts", "allergy", 100),
            MemoryFact.Create("My meeting is at 3pm", "schedule", 80),
            MemoryFact.Create("I prefer email over Slack", "preference", 50)
        };

        var result = await _reducerEvaluator.EvaluateAsync(agent, facts, 20, ct);
        return (result.FidelityScore, false, null);
    }
}
