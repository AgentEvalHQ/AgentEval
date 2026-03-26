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
    private int? _targetTokensOverride;

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
    public Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        CancellationToken cancellationToken = default)
        => RunBenchmarkAsync(agent, benchmark, progress: null, cancellationToken);

    /// <inheritdoc />
    public async Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        IProgress<BenchmarkProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(benchmark);

        _targetTokensOverride = benchmark.TargetTokensOverride;
        var totalStopwatch = Stopwatch.StartNew();
        var categoryResults = new List<BenchmarkCategoryResult>();
        var totalCategories = benchmark.Categories.Count;

        _logger.LogInformation("Starting memory benchmark: {BenchmarkName} ({CategoryCount} categories)",
            benchmark.Name, totalCategories);

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

            if (progress is not null)
            {
                var elapsed = totalStopwatch.Elapsed;
                var completed = categoryResults.Count;
                var remaining = totalCategories - completed;
                var estimatedRemaining = completed > 0
                    ? TimeSpan.FromTicks((long)(elapsed.Ticks / (double)completed * remaining))
                    : TimeSpan.Zero;

                progress.Report(new BenchmarkProgress
                {
                    CategoryName = catResult.CategoryName,
                    Score = catResult.Score,
                    Skipped = catResult.Skipped,
                    CompletedCategories = completed,
                    TotalCategories = totalCategories,
                    Elapsed = elapsed,
                    EstimatedRemaining = estimatedRemaining
                });
            }
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
                BenchmarkScenarioType.ConflictResolution => await RunConflictResolutionAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.MultiSessionReasoning => await RunMultiSessionReasoningAsync(agent, presetName, cancellationToken),
                BenchmarkScenarioType.PreferenceExtraction => await RunPreferenceExtractionAsync(agent, presetName, cancellationToken),
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
    /// Loads a scenario from JSON, injects context pressure, builds a MemoryTestScenario,
    /// and runs it. Returns null if the JSON file doesn't exist (caller falls back to hardcoded).
    /// </summary>
    private async Task<(double Score, bool Skipped, string? SkipReason)?> TryRunFromJsonAsync(
        IEvaluableAgent agent, string scenarioName, string presetName, CancellationToken ct)
    {
        try
        {
            var scenarioDef = DataLoading.ScenarioLoader.Load(scenarioName);
            var preset = DataLoading.ScenarioLoader.ResolvePreset(scenarioDef, presetName);

            // Apply target_tokens override from benchmark (e.g., Overflow preset)
            if (_targetTokensOverride.HasValue && preset.ContextPressure != null)
            {
                preset.ContextPressure.TargetTokens = _targetTokensOverride.Value;
            }

            // Load corpus turns (if configured)
            List<(string User, string Assistant)>? corpusTurns = null;
            if (preset.ContextPressure != null)
            {
                try
                {
                    if (preset.ContextPressure.TargetTokens.HasValue && preset.ContextPressure.TargetTokens.Value > 0)
                    {
                        corpusTurns = DataLoading.CorpusLoader.LoadToTargetTokens(
                            preset.ContextPressure.Corpus, preset.ContextPressure.TargetTokens.Value).ToList();
                    }
                    else
                    {
                        corpusTurns = DataLoading.CorpusLoader.Load(
                            preset.ContextPressure.Corpus,
                            preset.ContextPressure.MaxTurns ?? 15).ToList();
                    }
                }
                catch { /* corpus not available — continue without pressure */ }
            }

            // Inject themed distractor turns at deterministic positions in the corpus
            if (corpusTurns != null && preset.ContextPressure?.DistractorTurns is { Count: > 0 } distractorTurns)
            {
                var rng = new Random(42); // deterministic seed for reproducibility
                foreach (var d in distractorTurns)
                {
                    var insertAt = rng.Next(0, corpusTurns.Count);
                    corpusTurns.Insert(insertAt, (d.User, d.Assistant));
                }
            }

            // Separate facts into positioned (buried in corpus) and unpositioned (appended after)
            var positionedFacts = preset.Facts.Where(f => f.FractionalPosition.HasValue).ToList();
            var unpositionedFacts = preset.Facts.Where(f => !f.FractionalPosition.HasValue).ToList();

            // If we have positioned facts AND a corpus, interleave them
            if (positionedFacts.Count > 0 && corpusTurns != null && corpusTurns.Count > 0
                && agent is IHistoryInjectableAgent injectable)
            {
                var interleaved = BuildInterleavedHistory(corpusTurns, positionedFacts, preset.NoiseBetweenFacts, preset.ContextPressure?.SessionsCount ?? 0);
                injectable.InjectConversationHistory(interleaved);
            }
            else if (corpusTurns != null && agent is IHistoryInjectableAgent injectableNoPos)
            {
                // No positioned facts — inject corpus as before
                injectableNoPos.InjectConversationHistory(corpusTurns);
            }

            // Build steps from unpositioned facts (appended after corpus, current behavior)
            var steps = new List<MemoryStep>();
            var noiseIndex = positionedFacts.Count; // continue noise rotation

            foreach (var fact in unpositionedFacts)
            {
                var plantedText = fact.PlantedAs ?? fact.Content;
                if (fact.Timestamp != null)
                    plantedText = $"[{fact.Timestamp}] {plantedText}";
                steps.Add(MemoryStep.Fact(plantedText));

                if (preset.NoiseBetweenFacts.Count > 0)
                {
                    var noiseMsg = preset.NoiseBetweenFacts[noiseIndex % preset.NoiseBetweenFacts.Count];
                    steps.Add(MemoryStep.Noise(noiseMsg));
                    noiseIndex++;
                }
            }

            // Build queries
            var queries = new List<MemoryQuery>();
            foreach (var q in preset.Queries)
            {
                // For temporal queries, prepend today's date so the agent can reason about recency
                var queryQuestion = q.Question;
                if (string.Equals(q.QueryType, "temporal", StringComparison.OrdinalIgnoreCase))
                    queryQuestion = $"Today's date is {DateTimeOffset.UtcNow:yyyy-MM-dd}.\n\n{queryQuestion}";

                // Determine if this is an abstention query (explicit flag or query_type)
                var isAbstention = q.Abstention || string.Equals(q.QueryType, "abstention", StringComparison.OrdinalIgnoreCase);

                if (isAbstention)
                {
                    var forbidden = (q.ForbiddenFacts ?? [])
                        .Select(f => MemoryFact.Create(f)).ToArray();
                    var absQuery = MemoryQuery.CreateAbstention(queryQuestion, forbidden);
                    // Ensure query_type metadata is set for abstention too
                    if (q.QueryType != null)
                        absQuery.Metadata!["query_type"] = q.QueryType;
                    queries.Add(absQuery);
                }
                else
                {
                    var expected = q.ExpectedFacts
                        .Select(f => MemoryFact.Create(f)).ToArray();
                    var forbidden = (q.ForbiddenFacts ?? [])
                        .Select(f => MemoryFact.Create(f)).ToArray();

                    // Build metadata with query_type when specified
                    Dictionary<string, object>? metadata = null;
                    if (q.QueryType != null)
                    {
                        metadata = new Dictionary<string, object> { ["query_type"] = q.QueryType };
                    }

                    queries.Add(new MemoryQuery
                    {
                        Question = queryQuestion,
                        ExpectedFacts = expected,
                        ForbiddenFacts = forbidden,
                        Metadata = metadata
                    });
                }
            }

            var scenario = new MemoryTestScenario
            {
                Name = scenarioDef.Name,
                Description = scenarioDef.Description ?? "",
                Steps = steps,
                Queries = queries
            };

            var result = await _runner.RunAsync(agent, scenario, ct);
            return (result.OverallScore, false, null);
        }
        catch (FileNotFoundException)
        {
            return null; // JSON not found — caller should fall back
        }
    }

    /// <summary>
    /// Merges corpus turns with positioned facts, interleaving facts at their specified
    /// fractional positions within the corpus. This buries facts deep in context instead
    /// of appending them at the end (which exploits LLM recency bias).
    /// When <paramref name="sessionsCount"/> is greater than 0, the corpus is divided into
    /// that many segments with session boundary markers inserted between them.
    /// Facts with a <see cref="DataLoading.FactDefinition.SessionId"/> are placed within
    /// the corresponding session segment instead of using fractional positioning.
    /// </summary>
    internal static IEnumerable<(string User, string Assistant)> BuildInterleavedHistory(
        List<(string User, string Assistant)> corpusTurns,
        List<DataLoading.FactDefinition> positionedFacts,
        List<string> noiseBetweenFacts,
        int sessionsCount = 0)
    {
        if (sessionsCount <= 0)
        {
            // Original behavior — no session boundaries
            return BuildInterleavedHistoryNoSessions(corpusTurns, positionedFacts, noiseBetweenFacts);
        }

        // --- Session-aware interleaving ---
        // Divide corpus turns into sessionsCount equal segments
        var turnsPerSession = corpusTurns.Count / sessionsCount;
        var remainder = corpusTurns.Count % sessionsCount;

        // Build session segments (distribute remainder turns across first segments)
        var segments = new List<List<(string, string)>>();
        var offset = 0;
        for (int s = 0; s < sessionsCount; s++)
        {
            var count = turnsPerSession + (s < remainder ? 1 : 0);
            segments.Add(corpusTurns.GetRange(offset, count));
            offset += count;
        }

        // Separate facts by session assignment
        var sessionFacts = positionedFacts.Where(f => f.SessionId.HasValue).ToList();
        var fractionalFacts = positionedFacts.Where(f => !f.SessionId.HasValue).ToList();

        // Insert fractional-position facts into their computed segment
        // (same logic as before but scoped to the whole corpus)
        foreach (var (fact, i) in fractionalFacts.Select((f, i) => (f, i)))
        {
            var globalIndex = Math.Clamp((int)(fact.FractionalPosition!.Value * corpusTurns.Count), 0, corpusTurns.Count);
            // Determine which segment this index falls into
            var cumulative = 0;
            for (int s = 0; s < segments.Count; s++)
            {
                if (globalIndex <= cumulative + segments[s].Count || s == segments.Count - 1)
                {
                    var localIndex = Math.Clamp(globalIndex - cumulative, 0, segments[s].Count);
                    InsertFactIntoSegment(segments[s], localIndex, fact, i, noiseBetweenFacts);
                    break;
                }
                cumulative += segments[s].Count;
            }
        }

        // Insert session-assigned facts into the middle of their segment
        var noiseIdx = fractionalFacts.Count;
        foreach (var fact in sessionFacts)
        {
            var sessionIndex = Math.Clamp(fact.SessionId!.Value - 1, 0, segments.Count - 1);
            var segment = segments[sessionIndex];
            var midpoint = segment.Count / 2;
            InsertFactIntoSegment(segment, midpoint, fact, noiseIdx, noiseBetweenFacts);
            noiseIdx++;
        }

        // Generate session boundary dates spread across the last 6 months
        var today = DateTime.Today;
        var totalDays = 180; // ~6 months
        var dayStep = sessionsCount > 1 ? totalDays / (sessionsCount - 1) : 0;

        // Assemble final result with session markers between segments
        var result = new List<(string, string)>();
        for (int s = 0; s < segments.Count; s++)
        {
            // Insert session boundary marker before each segment
            var daysAgo = sessionsCount > 1
                ? totalDays - (s * dayStep)
                : 0;
            var sessionDate = today.AddDays(-daysAgo);
            var dateStr = sessionDate.ToString("yyyy-MM-dd");
            result.Add(($"--- Session {s + 1} ({dateStr}) ---", "Starting a new conversation session."));

            result.AddRange(segments[s]);
        }

        return result;
    }

    /// <summary>
    /// Inserts a fact (and optional noise) into a segment at the given local index.
    /// </summary>
    private static void InsertFactIntoSegment(
        List<(string, string)> segment, int localIndex, DataLoading.FactDefinition fact,
        int noiseIndex, List<string> noiseBetweenFacts)
    {
        var plantedText = fact.PlantedAs ?? fact.Content;
        if (fact.Timestamp != null)
            plantedText = $"[{fact.Timestamp}] {plantedText}";
        var assistantReply = fact.AssistantResponse ?? "Got it, I'll remember that.";
        var factTurn = (plantedText, assistantReply);

        if (noiseBetweenFacts.Count > 0)
        {
            var noiseMsg = noiseBetweenFacts[noiseIndex % noiseBetweenFacts.Count];
            var noiseTurn = (noiseMsg, "That's an interesting point.");
            segment.Insert(Math.Min(localIndex, segment.Count), noiseTurn);
        }

        segment.Insert(Math.Min(localIndex, segment.Count), factTurn);
    }

    /// <summary>
    /// Original interleaving logic without session boundaries.
    /// </summary>
    private static IEnumerable<(string User, string Assistant)> BuildInterleavedHistoryNoSessions(
        List<(string User, string Assistant)> corpusTurns,
        List<DataLoading.FactDefinition> positionedFacts,
        List<string> noiseBetweenFacts)
    {
        // Calculate insertion indices, sort descending so we insert from back to front
        // (prevents earlier insertions from shifting later indices)
        var insertions = positionedFacts
            .Select((fact, i) => (
                Index: Math.Clamp((int)(fact.FractionalPosition!.Value * corpusTurns.Count), 0, corpusTurns.Count),
                Fact: fact,
                NoiseIndex: i))
            .OrderByDescending(x => x.Index)
            .ToList();

        var result = new List<(string, string)>(corpusTurns);

        foreach (var ins in insertions)
        {
            var plantedText = ins.Fact.PlantedAs ?? ins.Fact.Content;
            if (ins.Fact.Timestamp != null)
                plantedText = $"[{ins.Fact.Timestamp}] {plantedText}";
            var assistantReply = ins.Fact.AssistantResponse ?? "Got it, I'll remember that.";
            var factTurn = (plantedText, assistantReply);

            // Optionally insert a noise turn after the fact (so fact doesn't sit right next to a query)
            if (noiseBetweenFacts.Count > 0)
            {
                var noiseMsg = noiseBetweenFacts[ins.NoiseIndex % noiseBetweenFacts.Count];
                var noiseTurn = (noiseMsg, "That's an interesting point.");
                result.Insert(ins.Index, noiseTurn);
            }

            result.Insert(ins.Index, factTurn);
        }

        return result;
    }

    /// <summary>
    /// Pre-fills the agent's conversation history with corpus-loaded turns to simulate
    /// a long conversation. This tests whether the agent can recall facts planted
    /// AFTER a large context has already been established — without expensive LLM calls.
    /// Falls back to SyntheticHistoryGenerator if corpus files are not available.
    /// </summary>
    private async Task<(double Score, bool Skipped, string? SkipReason)> RunPreferenceExtractionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var jsonResult = await TryRunFromJsonAsync(agent, "preference-extraction", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        // No hardcoded fallback — preference extraction is JSON-only
        return (0, true, "Preference extraction scenario JSON not found");
    }

    private static void InjectContextPressure(IEvaluableAgent agent, string presetName)
    {
        if (agent is not IHistoryInjectableAgent injectable) return;

        var (corpusName, turnCount) = presetName switch
        {
            "Diagnostic" => ("context-stress", 250), // Diagnostic uses stress corpus (~120K tokens)
            "Full" => ("context-stress", 200),       // Full uses stress corpus (~130K tokens)
            "Standard" => ("context-stress", 100),   // Standard uses stress corpus (~65K tokens)
            _ => ("context-small", 15)               // Quick uses all 15 small turns (~8K tokens)
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
        // Try JSON-driven scenario first
        var jsonResult = await TryRunFromJsonAsync(agent, "basic-retention", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        // Fallback: hardcoded scenario (legacy — will be removed once JSON migration is verified)
        var scores = new List<double>();
        InjectContextPressure(agent, presetName);

        // Quick+: Facts planted conversationally
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
        var jsonResult = await TryRunFromJsonAsync(agent, "temporal-reasoning", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        var scores = new List<double>();
        InjectContextPressure(agent, presetName);

        // Fallback: Quick+: Sequence ordering with 6 events (more events = harder to order correctly)
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
        var jsonResult = await TryRunFromJsonAsync(agent, "noise-resilience", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        var scores = new List<double>();
        InjectContextPressure(agent, presetName);

        // Fallback: Quick+: 4 facts buried in heavy noise (ratio 5:1 instead of default 3:1)
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
        // Note: FactUpdate JSON doesn't fully capture the update flow yet (needs initial + corrected facts)
        // so we keep the hardcoded version for now

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
        var jsonResult = await TryRunFromJsonAsync(agent, "multi-topic", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        var scores = new List<double>();

        // Fallback: Quick+: Basic multi-topic recall
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

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunMultiSessionReasoningAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        if (agent is not ISessionResettableAgent)
            return (0, true, "Agent does not implement ISessionResettableAgent");

        // Multi-session reasoning: plant partial info in Session 1, reset, plant more in Session 2, query
        try
        {
            var scenarioDef = DataLoading.ScenarioLoader.Load("multi-session-reasoning");
            var preset = DataLoading.ScenarioLoader.ResolvePreset(scenarioDef, presetName);

            if (preset.Facts.Count < 2)
                return (0, true, "Multi-session reasoning needs at least 2 facts");

            // Session 1: plant first half of facts
            var midpoint = preset.Facts.Count / 2;
            for (int i = 0; i < midpoint; i++)
            {
                var text = preset.Facts[i].PlantedAs ?? preset.Facts[i].Content;
                await agent.InvokeAsync(text, ct);
            }

            // Reset session
            await ((ISessionResettableAgent)agent).ResetSessionAsync(ct);

            // Session 2: plant second half of facts
            for (int i = midpoint; i < preset.Facts.Count; i++)
            {
                var text = preset.Facts[i].PlantedAs ?? preset.Facts[i].Content;
                await agent.InvokeAsync(text, ct);
            }

            // Now query — requires combining info from both sessions
            var queries = preset.Queries.Select(q =>
                MemoryQuery.Create(q.Question,
                    q.ExpectedFacts.Select(f => MemoryFact.Create(f)).ToArray())).ToList();

            var scenario = new MemoryTestScenario
            {
                Name = scenarioDef.Name,
                Description = scenarioDef.Description ?? "",
                Steps = [], // Facts already planted above
                Queries = queries
            };

            var result = await _runner.RunAsync(agent, scenario, ct);
            return (result.OverallScore, false, null);
        }
        catch (FileNotFoundException)
        {
            return (0, true, "Multi-session reasoning scenario JSON not found");
        }
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunConflictResolutionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var jsonResult = await TryRunFromJsonAsync(agent, "conflict-resolution", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        // No hardcoded fallback — conflict resolution is JSON-only
        return (0, true, "Conflict resolution scenario JSON not found");
    }

    private async Task<(double Score, bool Skipped, string? SkipReason)> RunAbstentionAsync(
        IEvaluableAgent agent, string presetName, CancellationToken ct)
    {
        var jsonResult = await TryRunFromJsonAsync(agent, "abstention", presetName, ct);
        if (jsonResult.HasValue) return jsonResult.Value;

        // Fallback: hardcoded abstention
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
