// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Runs the LongMemEval benchmark against an agent using the official evaluation methodology:
/// history injection (0 LLM calls), query (1 LLM call), type-specific judge (1 LLM call).
/// Supports stratified sampling, binary scoring, and session-boundary-preserving history formatting.
/// </summary>
public class LongMemEvalBenchmarkRunner : IExternalBenchmarkRunner
{
    private readonly IChatClient _chatClient;
    private string? _datasetPath;
    private readonly ILogger<LongMemEvalBenchmarkRunner> _logger;

    public string BenchmarkId => "longmemeval";
    public string DisplayName => "LongMemEval (ICLR 2025)";

    public LongMemEvalBenchmarkRunner(
        IChatClient chatClient,
        ILogger<LongMemEvalBenchmarkRunner>? logger = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _datasetPath = null;
        _logger = logger ?? NullLogger<LongMemEvalBenchmarkRunner>.Instance;
    }

    /// <summary>
    /// Factory method matching the MemoryBenchmarkRunner.Create pattern.
    /// </summary>
    public static LongMemEvalBenchmarkRunner Create(IChatClient chatClient, string? datasetPath = null)
        => new(chatClient) { _datasetPath = datasetPath };

    /// <summary>
    /// Runs the LongMemEval benchmark.
    /// </summary>
    public async Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        AgentBenchmarkConfig config,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        // 1. Load data
        var entries = LoadEntries(options);
        _logger.LogInformation(
            "LongMemEval: loaded {Count} questions ({Mode} mode, stratified={Stratified})",
            entries.Count, options.DatasetMode, options.StratifiedSampling);

        var judge = new LongMemEvalJudge(_chatClient, NullLogger<LongMemEvalJudge>.Instance);
        var totalStopwatch = Stopwatch.StartNew();
        var questionResults = new List<QuestionResult>();
        var totalLlmCalls = 0;

        // 2. Run each question
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = entries[i];
            var qStopwatch = Stopwatch.StartNew();

            _logger.LogDebug(
                "Running question [{Index}/{Total}] {QuestionId} type={Type}",
                i + 1, entries.Count, entry.QuestionId, entry.QuestionType);

            // Reset agent
            if (agent is ISessionResettableAgent resettable)
                await resettable.ResetSessionAsync(ct);

            // Inject history (0 LLM calls) or fall back to text blob prepended to query
            string? textBlobPrefix = null;
            if (agent is IHistoryInjectableAgent injectable)
            {
                var history = LongMemEvalHistoryFormatter.Format(entry, options);
                injectable.InjectConversationHistory(history);
            }
            else
            {
                _logger.LogWarning(
                    "Agent does not implement IHistoryInjectableAgent — using text blob fallback for {QuestionId}",
                    entry.QuestionId);
                textBlobPrefix = LongMemEvalHistoryFormatter.FormatAsTextBlob(entry, options);
            }

            // Query (1 LLM call)
            var queryPrompt = textBlobPrefix != null
                ? $"{textBlobPrefix}\nQuestion: {entry.Question}\nAnswer:"
                : entry.Question;
            if (textBlobPrefix == null && !string.IsNullOrEmpty(entry.QuestionDate))
                queryPrompt = $"Current Date: {entry.QuestionDate}\n\n{queryPrompt}";

            var response = await agent.InvokeAsync(queryPrompt, ct);
            totalLlmCalls++;

            // Judge (1 LLM call)
            var question = new ExternalBenchmarkQuestion
            {
                QuestionId = entry.QuestionId,
                QuestionType = entry.QuestionType,
                Question = entry.Question,
                GoldAnswer = entry.Answer,
                QuestionDate = entry.QuestionDate,
                IsAbstention = entry.IsAbstention
            };

            var judgment = await judge.JudgeAsync(response.Text, question, ct);
            totalLlmCalls++;

            qStopwatch.Stop();

            questionResults.Add(new QuestionResult
            {
                QuestionId = entry.QuestionId,
                QuestionType = entry.QuestionType,
                Question = entry.Question,
                GoldAnswer = entry.Answer,
                AgentResponse = response.Text,
                Correct = judgment.Correct,
                RawScore = judgment.RawScore,
                JudgeExplanation = judgment.Explanation,
                Duration = qStopwatch.Elapsed
            });

            _logger.LogInformation(
                "[{Index}/{Total}] {Type,-30} {Correct}",
                i + 1, entries.Count, entry.QuestionType,
                judgment.Correct ? "CORRECT" : "WRONG");
        }

        totalStopwatch.Stop();

        // 3. Aggregate results
        return AggregateResults(questionResults, options, totalStopwatch.Elapsed, totalLlmCalls);
    }

    private IReadOnlyList<LongMemEvalEntry> LoadEntries(ExternalBenchmarkOptions options)
    {
        var path = options.DatasetPath ?? _datasetPath;

        return path != null
            ? LongMemEvalDataLoader.LoadFromFile(path, options)
            : LongMemEvalDataLoader.LoadEmbedded(options);
    }

    private ExternalBenchmarkResult AggregateResults(
        List<QuestionResult> questionResults,
        ExternalBenchmarkOptions options,
        TimeSpan duration,
        int totalLlmCalls)
    {
        // Per-type results: group by the 6 original question types.
        // Abstention questions (_abs suffix) stay in their original type for per-type reporting,
        // matching the official LongMemEval evaluation methodology.
        var perType = questionResults
            .GroupBy(q => q.QuestionType)
            .ToDictionary(
                g => g.Key,
                g => new TypeResult
                {
                    TypeName = g.Key,
                    TotalQuestions = g.Count(),
                    CorrectQuestions = g.Count(q => q.Correct),
                    Duration = TimeSpan.FromTicks(g.Sum(q => q.Duration.Ticks))
                });

        // Micro-average (overall)
        var totalCorrect = questionResults.Count(q => q.Correct);
        var overallAccuracy = questionResults.Count > 0
            ? (double)totalCorrect / questionResults.Count * 100
            : 0;

        // Macro-average (task-averaged: mean of per-type accuracies across the 6 types)
        var taskAveraged = perType.Count > 0
            ? perType.Values.Average(t => t.Accuracy)
            : 0;

        return new ExternalBenchmarkResult
        {
            BenchmarkId = BenchmarkId,
            BenchmarkName = options.DatasetMode != null
                ? $"LongMemEval-{options.DatasetMode} {questionResults.Count}q"
                : $"LongMemEval {questionResults.Count}q",
            OverallAccuracy = overallAccuracy,
            TaskAveragedAccuracy = taskAveraged,
            PerTypeResults = perType,
            QuestionResults = questionResults,
            Duration = duration,
            TotalLlmCalls = totalLlmCalls,
            Options = options
        };
    }
}
