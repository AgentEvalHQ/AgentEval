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

    /// <summary>
    /// Preset-baked options that the runner uses when <c>RunAsync</c> is called via the
    /// parameterless preset entry point (or when the caller passes <c>null</c> options).
    /// Phase 8 (v0.10.0-beta): replaces the dead-<c>RandomSeed</c>/dead-<c>MaxQuestions</c>
    /// footgun where Subset()/Full() returned a runner that ignored its preset configuration
    /// unless the caller manually passed <c>LongMemEvalBenchmark.SubsetOptions</c>.
    /// </summary>
    public ExternalBenchmarkOptions? DefaultOptions { get; private set; }

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
    /// Factory method that bakes a preset's <see cref="ExternalBenchmarkOptions"/> into the
    /// returned runner. Phase 8 (v0.10.0-beta) — fixes the dead-options footgun where
    /// callers had to manually pass <c>LongMemEvalBenchmark.SubsetOptions</c> to get the
    /// preset's intended sampling / seed behaviour.
    /// </summary>
    /// <param name="chatClient">Required <see cref="IChatClient"/> for the LLM judge.</param>
    /// <param name="datasetPath">Optional path to the full LongMemEval dataset.</param>
    /// <param name="defaultOptions">
    /// Preset-baked options. When the caller invokes <c>RunAsync(agent, config)</c> (or
    /// passes <c>null</c> for options on the full overload), these defaults are applied.
    /// </param>
    public static LongMemEvalBenchmarkRunner Create(
        IChatClient chatClient,
        string? datasetPath,
        ExternalBenchmarkOptions? defaultOptions)
        => new(chatClient) { _datasetPath = datasetPath, DefaultOptions = defaultOptions };

    /// <summary>
    /// Convenience overload that runs the benchmark using the runner's
    /// <see cref="DefaultOptions"/>. Phase 8 (v0.10.0-beta): closes the dead-options
    /// footgun where callers had to manually pass <c>LongMemEvalBenchmark.SubsetOptions</c>
    /// to get the preset's intended sampling / seed behaviour.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <see cref="DefaultOptions"/> is null (runner was not constructed via the options-baking factory).</exception>
    public Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        AgentBenchmarkConfig config,
        CancellationToken ct = default)
    {
        if (DefaultOptions is null)
        {
            throw new InvalidOperationException(
                "LongMemEvalBenchmarkRunner.RunAsync(agent, config, ct) requires the runner to have been " +
                "constructed via Create(client, datasetPath, defaultOptions) so DefaultOptions is populated. " +
                "Either pass options explicitly via the 4-arg RunAsync overload, or use the preset factories " +
                "in LongMemEvalBenchmark which bake the options for you.");
        }
        return RunAsync(agent, config, DefaultOptions, ct);
    }

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
            var injectionMode = options.HistoryInjectionMode;

            var useStructured = injectionMode switch
            {
                HistoryInjectionMode.StructuredChatHistory => true,
                HistoryInjectionMode.TextBlob => false,
                _ => agent is IHistoryInjectableAgent // Auto: use structured if available
            };

            if (useStructured && agent is IHistoryInjectableAgent injectable)
            {
                var history = LongMemEvalHistoryFormatter.Format(entry, options);
                injectable.InjectConversationHistory(history);
            }
            else
            {
                if (injectionMode == HistoryInjectionMode.StructuredChatHistory && agent is not IHistoryInjectableAgent)
                {
                    _logger.LogWarning(
                        "HistoryInjectionMode is StructuredChatHistory but agent does not implement IHistoryInjectableAgent — falling back to text blob for {QuestionId}",
                        entry.QuestionId);
                }
                else if (injectionMode == HistoryInjectionMode.TextBlob)
                {
                    _logger.LogDebug(
                        "Using text blob injection mode (configured) for {QuestionId}",
                        entry.QuestionId);
                }
                else
                {
                    _logger.LogWarning(
                        "Agent does not implement IHistoryInjectableAgent — using text blob fallback for {QuestionId}",
                        entry.QuestionId);
                }

                textBlobPrefix = LongMemEvalHistoryFormatter.FormatAsTextBlob(entry, options);
            }

            // Query (1 LLM call)
            var queryPrompt = textBlobPrefix != null
                ? $"{textBlobPrefix}\nQuestion: {entry.Question}\nAnswer:"
                : entry.Question;
            if (textBlobPrefix == null && !string.IsNullOrEmpty(entry.QuestionDate))
                queryPrompt = $"Current Date: {entry.QuestionDate}\n\n{queryPrompt}";

            try
            {
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

                var correctLabel = judgment.Correct ? "CORRECT" : "WRONG";
                _logger.LogInformation(
                    "[{Index}/{Total}] {Type,-30} {Correct}  ({Elapsed:F1}s)",
                    i + 1, entries.Count, entry.QuestionType,
                    correctLabel, qStopwatch.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                qStopwatch.Stop();
                var errorMsg = ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                    ? "CONTENT_FILTER"
                    : $"ERROR: {ex.Message}";

                questionResults.Add(new QuestionResult
                {
                    QuestionId = entry.QuestionId,
                    QuestionType = entry.QuestionType,
                    Question = entry.Question,
                    GoldAnswer = entry.Answer,
                    AgentResponse = $"[{errorMsg}]",
                    Correct = false,
                    RawScore = 0,
                    JudgeExplanation = $"Skipped due to error: {ex.Message}",
                    Duration = qStopwatch.Elapsed
                });

                _logger.LogWarning(
                    "[{Index}/{Total}] {Type,-30} {Error} — {QuestionId}  ({Elapsed:F1}s)",
                    i + 1, entries.Count, entry.QuestionType, errorMsg, entry.QuestionId, qStopwatch.Elapsed.TotalSeconds);
            }
        }

        totalStopwatch.Stop();

        // 3. Aggregate results
        return AggregateResults(questionResults, options, totalStopwatch.Elapsed, totalLlmCalls);
    }

    private IReadOnlyList<LongMemEvalEntry> LoadEntries(ExternalBenchmarkOptions options)
    {
        // v0.10.1-beta: no embedded fallback any more. Resolution order:
        // explicit options.DatasetPath -> runner-baked _datasetPath -> LONGMEMEVAL_DATASET_PATH env var
        // -> canonical local path under workspace root -> throw LongMemEvalDatasetNotFoundException.
        var explicitPath = options.DatasetPath ?? _datasetPath;
        var resolved = LongMemEvalDataLoader.ResolveDatasetPath(explicitPath);
        if (resolved == null)
            throw LongMemEvalDatasetNotFoundException.ForResolutionFailure();

        return LongMemEvalDataLoader.LoadFromFile(resolved, options);
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
