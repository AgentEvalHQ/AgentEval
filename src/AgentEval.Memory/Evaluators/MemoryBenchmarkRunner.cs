// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Runs comprehensive memory benchmark suites against agents.
/// Orchestrates scenario execution across multiple categories and produces holistic scores.
/// Supports scenario depth: Quick=1 scenario, Standard=2, Full=3+ per category.
/// </summary>
public class MemoryBenchmarkRunner : IMemoryBenchmarkRunner
{
    /// <summary>
    /// Creates a fully-wired benchmark runner with sensible defaults.
    /// This is the simplest way to get started — no DI, no manual wiring.
    /// <example>
    /// <code>
    /// var runner = MemoryBenchmarkRunner.Create(chatClient);
    /// var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Quick);
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="chatClient">The LLM client used by the memory judge for scoring.</param>
    /// <returns>A ready-to-use benchmark runner.</returns>
    public static MemoryBenchmarkRunner Create(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var testRunner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);

        return new MemoryBenchmarkRunner(
            testRunner,
            judge,
            new ReachBackEvaluator(testRunner, judge, NullLogger<ReachBackEvaluator>.Instance),
            new ReducerEvaluator(testRunner, NullLogger<ReducerEvaluator>.Instance),
            new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance),
            new MemoryScenarios(),
            new ChattyConversationScenarios(),
            new TemporalMemoryScenarios(),
            new CrossSessionScenarios(),
            NullLogger<MemoryBenchmarkRunner>.Instance);
    }

    private readonly IMemoryTestRunner _runner;
    private readonly IMemoryJudge _judge;
    private readonly IReachBackEvaluator _reachBackEvaluator;
    private readonly IReducerEvaluator _reducerEvaluator;
    private readonly ICrossSessionEvaluator _crossSessionEvaluator;
    private readonly IMemoryScenarios _memoryScenarios;
    private readonly IChattyConversationScenarios _chattyScenarios;
    private readonly ITemporalMemoryScenarios _temporalScenarios;
    private readonly ICrossSessionScenarios _crossSessionScenarios;
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
        : this(runner, judge, reachBackEvaluator, reducerEvaluator, crossSessionEvaluator,
               memoryScenarios, chattyScenarios, temporalScenarios,
               new CrossSessionScenarios(), logger)
    {
    }

    public MemoryBenchmarkRunner(
        IMemoryTestRunner runner,
        IMemoryJudge judge,
        IReachBackEvaluator reachBackEvaluator,
        IReducerEvaluator reducerEvaluator,
        ICrossSessionEvaluator crossSessionEvaluator,
        IMemoryScenarios memoryScenarios,
        IChattyConversationScenarios chattyScenarios,
        ITemporalMemoryScenarios temporalScenarios,
        ICrossSessionScenarios crossSessionScenarios,
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
        _crossSessionScenarios = crossSessionScenarios;
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
            if (agent is ISessionResettableAgent resettable)
            {
                await resettable.ResetSessionAsync(cancellationToken);
            }

            _logger.LogDebug("Running benchmark category: {CategoryName}", category.Name);

            var catResult = await RunCategoryAsync(agent, category, benchmark.Name, cancellationToken);
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

    private async Task<BenchmarkCategoryResult> RunCategoryAsync(
        IEvaluableAgent agent,
        MemoryBenchmarkCategory category,
        string presetName,
        CancellationToken cancellationToken)
    {
        var catStopwatch = Stopwatch.StartNew();

        try
        {
            var score = category.ScenarioType switch
            {
                BenchmarkScenarioType.BasicRetention => await RunBasicRetentionAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.TemporalReasoning => await RunTemporalReasoningAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.NoiseResilience => await RunNoiseResilienceAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.ReachBackDepth => await RunReachBackAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.FactUpdateHandling => await RunFactUpdateAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.MultiTopic => await RunMultiTopicAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.CrossSession => await RunCrossSessionAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.ReducerFidelity => await RunReducerFidelityAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.Abstention => await RunAbstentionAsync(agent, presetName, cancellationToken),
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

    /// <summary>
    /// Resets agent session between scenarios within a category to prevent cross-contamination.
    /// </summary>
    private async Task ResetBetweenScenarios(IEvaluableAgent agent, CancellationToken ct)
    {
        if (agent is ISessionResettableAgent resettable)
            await resettable.ResetSessionAsync(ct);
    }

    /// <summary>
    /// Pre-fills the agent's conversation history with corpus-loaded turns to simulate
    /// a long conversation. This tests whether the agent can recall facts planted
    /// AFTER a large context has already been established — without expensive LLM calls.
    /// Falls back to SyntheticHistoryGenerator if corpus files are not available.
    /// </summary>
    private static void InjectContextPressure(IEvaluableAgent agent, string presetName)
    {
        if (agent is not IHistoryInjectableAgent injectable) return;

        var (corpusName, turnCount) = presetName switch
        {
            "Full" => ("context-medium", 40),       // Full uses all 40 medium turns
            "Standard" => ("context-medium", 30),    // Standard uses 30 of 40 medium turns
            _ => ("context-small", 15)               // Quick uses all 15 small turns
        };

        try
        {
            var history = DataLoading.CorpusLoader.Load(corpusName, turnCount);
            injectable.InjectConversationHistory(history);
        }
        catch
        {
            // Fallback to SyntheticHistoryGenerator if corpus not available
            var fallback = SyntheticHistoryGenerator.Generate(turnCount);
            injectable.InjectConversationHistory(fallback);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category handlers — each runs 1 (Quick) / 2 (Standard) / 3+ (Full) scenarios
    // ═══════════════════════════════════════════════════════════════

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunBasicRetentionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Pre-fill context to simulate a long prior conversation
        InjectContextPressure(agent, presetName);

        // Quick+: Facts planted conversationally (not "My name is X" but natural phrasing)
        // with distractor conversation between facts and queries.
        MemoryFact[] facts =
        [
            MemoryFact.Create("My name is José and I'm originally from Barcelona"),
            MemoryFact.Create("I moved to Copenhagen about three years ago for work"),
            MemoryFact.Create("I have a severe peanut allergy — I carry an EpiPen"),
            MemoryFact.Create("I'm meeting my manager Sarah at 3pm tomorrow to discuss the Q2 roadmap"),
            MemoryFact.Create("I strongly prefer email over Slack for anything important"),
            MemoryFact.Create("My dog Max is a 4-year-old golden retriever"),
            MemoryFact.Create("I'm taking an online course in machine learning on weekends")
        ];

        // Queries are indirect — not "What is my name?" but questions that require
        // the agent to synthesize and recall specific details from context
        MemoryQuery[] queries =
        [
            MemoryQuery.Create("What health concerns should you keep in mind when suggesting food for me?", facts[2]),
            MemoryQuery.Create("If someone needed to reach me urgently about work, what communication channel should they use and why?", facts[4]),
            MemoryQuery.Create("I need to prepare for a meeting — who am I meeting, when, and what's it about?", facts[3]),
            MemoryQuery.Create("Tell me what you know about my living situation and background", facts[0], facts[1]),
            MemoryQuery.Create("What do you know about my personal life outside of work?", facts[5], facts[6])
        ];

        var scenario = _memoryScenarios.CreateBasicMemoryTest(facts, queries);
        var result = await _runner.RunAsync(agent, scenario, ct);
        scores.Add(result.OverallScore);

        // Standard+: Long-term memory (facts + 10 conversation turns, then query)
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var longTermScenario = _memoryScenarios.CreateLongTermMemoryTest(facts, conversationTurns: 10);
            var longTermResult = await _runner.RunAsync(agent, longTermScenario, ct);
            scores.Add(longTermResult.OverallScore);
        }

        // Full: Priority memory (high vs low importance)
        if (presetName is "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            MemoryFact[] highPriority = [MemoryFact.Create("I'm allergic to peanuts", "allergy", 100)];
            MemoryFact[] lowPriority = [MemoryFact.Create("I like the color blue", "preference", 20)];
            var priorityScenario = _memoryScenarios.CreatePriorityMemoryTest(highPriority, lowPriority);
            var priorityResult = await _runner.RunAsync(agent, priorityScenario, ct);
            scores.Add(priorityResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunTemporalReasoningAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Pre-fill context
        InjectContextPressure(agent, presetName);

        // Quick+: Sequence ordering with 6 events (more events = harder to order correctly)
        // Events are NOT in chronological order when planted — agent must sort them
        var sequenceScenario = _temporalScenarios.CreateSequenceMemoryTest(
        [
            MemoryFact.Create("Got promoted to mid-level developer", DateTimeOffset.UtcNow.AddMonths(-2)),
            MemoryFact.Create("Started learning Python on my own", DateTimeOffset.UtcNow.AddMonths(-18)),
            MemoryFact.Create("Completed an advanced React course", DateTimeOffset.UtcNow.AddMonths(-4)),
            MemoryFact.Create("Got my first junior developer job at a startup", DateTimeOffset.UtcNow.AddMonths(-12)),
            MemoryFact.Create("Switched to a larger company and learned C# there", DateTimeOffset.UtcNow.AddMonths(-8)),
            MemoryFact.Create("Led my first project as tech lead", DateTimeOffset.UtcNow.AddMonths(-1))
        ]);
        var seqResult = await _runner.RunAsync(agent, sequenceScenario, ct);
        scores.Add(seqResult.OverallScore);

        // Standard+: Time-point memory
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var timePointScenario = _temporalScenarios.CreateTimePointMemoryTest(
            [
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow.AddDays(-7),
                DateTimeOffset.UtcNow.AddDays(-1)
            ], eventsPerTimepoint: 2);
            var tpResult = await _runner.RunAsync(agent, timePointScenario, ct);
            scores.Add(tpResult.OverallScore);
        }

        // Full: Causal reasoning
        if (presetName is "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var causalScenario = _temporalScenarios.CreateCausalReasoningTest(
            [
                new List<MemoryFact>
                {
                    MemoryFact.Create("Started raining heavily", DateTimeOffset.UtcNow.AddHours(-3)),
                    MemoryFact.Create("The basement flooded", DateTimeOffset.UtcNow.AddHours(-2)),
                    MemoryFact.Create("Called a plumber to fix the damage", DateTimeOffset.UtcNow.AddHours(-1))
                }
            ]);
            var causalResult = await _runner.RunAsync(agent, causalScenario, ct);
            scores.Add(causalResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunNoiseResilienceAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Pre-fill context
        InjectContextPressure(agent, presetName);

        // Quick+: 4 facts buried in heavy noise (ratio 5:1 instead of default 3:1)
        // More facts = harder to recall all of them. Higher noise = more distraction.
        MemoryFact[] facts =
        [
            MemoryFact.Create("I'm severely allergic to peanuts and tree nuts", "allergy", 100),
            MemoryFact.Create("My dentist appointment is next Tuesday at 2:30pm", "schedule", 80),
            MemoryFact.Create("My WiFi password at home is BlueOcean42!", "credentials", 90),
            MemoryFact.Create("My car is parked in lot B, spot 247", "location", 70)
        ];
        var buriedScenario = _chattyScenarios.CreateBuriedFactsScenario(facts, noiseRatio: 5);
        var buriedResult = await _runner.RunAsync(agent, buriedScenario, ct);
        scores.Add(buriedResult.OverallScore);

        // Standard+: Topic switching with the same 4 facts
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var topicScenario = _chattyScenarios.CreateTopicSwitchingScenario(facts, topicChanges: 8);
            var topicResult = await _runner.RunAsync(agent, topicScenario, ct);
            scores.Add(topicResult.OverallScore);
        }

        // Full: Emotional distractors + false information with confusing contradictions
        if (presetName is "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var emotionalScenario = _chattyScenarios.CreateEmotionalDistractorScenario(facts);
            var emotionalResult = await _runner.RunAsync(agent, emotionalScenario, ct);
            scores.Add(emotionalResult.OverallScore);

            await ResetBetweenScenarios(agent, ct);
            MemoryFact[] falseFacts =
            [
                MemoryFact.Create("You are allergic to shellfish, not peanuts"),
                MemoryFact.Create("Your dentist appointment is on Thursday at 4pm"),
                MemoryFact.Create("Your car is in lot C, spot 112")
            ];
            var falseInfoScenario = _chattyScenarios.CreateFalseInformationScenario(facts, falseFacts);
            var falseResult = await _runner.RunAsync(agent, falseInfoScenario, ct);
            scores.Add(falseResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunReachBackAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var fact = MemoryFact.Create("I'm allergic to peanuts", "allergy", 100);
        var query = MemoryQuery.Create("What food allergies should you know about?", fact);

        // Quick: depths [5, 10, 25]  Standard: + [50]  Full: + [100]
        int[] depths = presetName switch
        {
            "Full" => [5, 10, 25, 50, 100],
            "Standard" => [5, 10, 25, 50],
            _ => [5, 10, 25]
        };

        var result = await _reachBackEvaluator.EvaluateAsync(agent, fact, query, depths, ct);
        return (result.OverallScore, false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunFactUpdateAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Quick+: Basic corrections
        var updateScenario = _memoryScenarios.CreateMemoryUpdateTest(
        [
            MemoryFact.Create("My favorite color is blue"),
            MemoryFact.Create("I drive a Honda Civic")
        ],
        [
            MemoryFact.Create("Actually, my favorite color is now green"),
            MemoryFact.Create("I sold the Honda and bought a Tesla")
        ]);
        var updateResult = await _runner.RunAsync(agent, updateScenario, ct);
        scores.Add(updateResult.OverallScore);

        // Standard+: Verify corrections stick after conversation
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            MemoryFact[] delayFacts =
            [
                MemoryFact.Create("My phone number is 555-0100"),
                MemoryFact.Create("Actually my new phone number is 555-0200")
            ];
            var delayScenario = _memoryScenarios.CreateLongTermMemoryTest(delayFacts, conversationTurns: 5);
            var delayResult = await _runner.RunAsync(agent, delayScenario, ct);
            scores.Add(delayResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunMultiTopicAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Quick+: Basic multi-topic recall
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
        scores.Add(result.OverallScore);

        // Standard+: Categorized memory
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var categorizedFacts = new Dictionary<string, List<MemoryFact>>
            {
                ["health"] = [MemoryFact.Create("I'm allergic to peanuts", "health", 100), MemoryFact.Create("I take vitamin D", "health", 50)],
                ["work"] = [MemoryFact.Create("I work at Contoso", "work", 70), MemoryFact.Create("My manager is Sarah", "work", 60)]
            };
            var catScenario = MemoryScenarios.CategorizedMemory(categorizedFacts);
            var catResult = await _runner.RunAsync(agent, catScenario, ct);
            scores.Add(catResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunCrossSessionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        if (agent is not ISessionResettableAgent)
        {
            return (0, true, "Agent does not implement ISessionResettableAgent");
        }

        var scores = new List<double>();

        // Quick+: Basic cross-session (3 facts, 1 reset)
        var facts = new List<MemoryFact>
        {
            MemoryFact.Create("My name is José"),
            MemoryFact.Create("I live in Copenhagen"),
            MemoryFact.Create("I'm allergic to peanuts")
        };
        var result = await _crossSessionEvaluator.EvaluateAsync(agent, facts, 0.8, ct);
        scores.Add(result.OverallScore);

        // Standard+: Multi-session with 3 resets
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var multiSessionScenario = _crossSessionScenarios.CreateCrossSessionMemoryTest(
                facts, sessionCount: 3, sessionGapMinutes: 30);
            var multiResult = await _runner.RunAsync(agent, multiSessionScenario, ct);
            scores.Add(multiResult.OverallScore);
        }

        // Full: Incremental learning across sessions
        if (presetName is "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var incrementalScenario = _crossSessionScenarios.CreateIncrementalLearningTest(
            [
                new List<MemoryFact> { MemoryFact.Create("My dog's name is Max") },
                new List<MemoryFact> { MemoryFact.Create("I work at Contoso") },
                new List<MemoryFact> { MemoryFact.Create("My birthday is March 15th") }
            ]);
            var incResult = await _runner.RunAsync(agent, incrementalScenario, ct);
            scores.Add(incResult.OverallScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunReducerFidelityAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var scores = new List<double>();

        // Quick+: Basic reducer fidelity (3 facts, 20 noise)
        var facts = new List<MemoryFact>
        {
            MemoryFact.Create("I'm allergic to peanuts", "allergy", 100),
            MemoryFact.Create("My meeting is at 3pm", "schedule", 80),
            MemoryFact.Create("I prefer email over Slack", "preference", 50)
        };
        var result = await _reducerEvaluator.EvaluateAsync(agent, facts, 20, ct);
        scores.Add(result.FidelityScore);

        // Standard+: More facts, more noise (5 facts, 40 noise)
        if (presetName is "Standard" or "Full")
        {
            await ResetBetweenScenarios(agent, ct);
            var moreFacts = new List<MemoryFact>
            {
                MemoryFact.Create("I'm allergic to peanuts", "allergy", 100),
                MemoryFact.Create("My meeting is at 3pm", "schedule", 80),
                MemoryFact.Create("I prefer email over Slack", "preference", 50),
                MemoryFact.Create("My dog's name is Max", "personal", 60),
                MemoryFact.Create("I work at Contoso", "work", 70)
            };
            var moreResult = await _reducerEvaluator.EvaluateAsync(agent, moreFacts, 40, ct);
            scores.Add(moreResult.FidelityScore);
        }

        return (scores.Average(), false, null);
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunAbstentionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        // Pre-fill context to make abstention harder (agent has lots of conversation to confuse it)
        InjectContextPressure(agent, presetName);

        // Plant a FEW real facts — so the agent has SOME info but not about every topic
        MemoryFact[] plantedFacts =
        [
            MemoryFact.Create("My name is José and I'm originally from Barcelona"),
            MemoryFact.Create("I work as a software engineer at a startup"),
            MemoryFact.Create("I have a golden retriever named Max")
        ];

        // Answerable queries (agent should recall these — planted facts)
        var answerableQueries = new List<MemoryQuery>
        {
            MemoryQuery.Create("What's my name and where am I from?", plantedFacts[0]),
            MemoryQuery.Create("What kind of pet do I have and what's its name?", plantedFacts[2])
        };

        // Abstention queries — agent must say "I don't know" (these facts were NEVER planted)
        var abstentionQueries = new List<MemoryQuery>
        {
            MemoryQuery.CreateAbstention("What's my sister's name?",
                MemoryFact.Create("any specific name")),
            MemoryQuery.CreateAbstention("What's my home address?",
                MemoryFact.Create("any specific address or street")),
            MemoryQuery.CreateAbstention("What's my phone number?",
                MemoryFact.Create("any specific phone number")),
            MemoryQuery.CreateAbstention("What did I have for breakfast this morning?",
                MemoryFact.Create("any specific food or meal")),
            MemoryQuery.CreateAbstention("What's my partner's occupation?",
                MemoryFact.Create("any specific job title or profession")),
            MemoryQuery.CreateAbstention("What car do I drive?",
                MemoryFact.Create("any specific car brand or model"))
        };

        // Build combined scenario: plant facts, then ask a mix of answerable + unanswerable
        var steps = plantedFacts.Select(f => MemoryStep.Fact(f.Content)).ToList();
        var allQueries = new List<MemoryQuery>();
        allQueries.AddRange(answerableQueries);
        allQueries.AddRange(abstentionQueries);

        var scenario = new MemoryTestScenario
        {
            Name = "Abstention — Hallucination Detection",
            Description = "Tests whether the agent correctly says 'I don't know' for information it was never given, while still recalling facts it WAS given.",
            Steps = steps,
            Queries = allQueries
        };

        var result = await _runner.RunAsync(agent, scenario, ct);
        return (result.OverallScore, false, null);
    }
}
