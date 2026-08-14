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
public partial class LongMemEvalBenchmarkRunner : IExternalBenchmarkRunner
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
        options.Validate();

        // 1. Load data
        var loaded = LoadEntries(options);
        return await RunSelectedAsync(agent, options, loaded.Entries, executionLabel: null, ct, loaded);
    }

    private async Task<ExternalBenchmarkResult> RunSelectedAsync(
        IEvaluableAgent agent,
        ExternalBenchmarkOptions options,
        IReadOnlyList<LongMemEvalEntry> entries,
        string? executionLabel,
        CancellationToken ct,
        LoadedDataset? dataset = null,
        OracleProjectionReport? oracleProjection = null)
    {
        _logger.LogInformation(
            "LongMemEval: loaded {Count} questions ({Mode} mode, stratified={Stratified})",
            entries.Count, options.DatasetMode, options.StratifiedSampling);

        var judge = new LongMemEvalJudge(_chatClient, NullLogger<LongMemEvalJudge>.Instance);
        // Null unless the caller asked to pin the answer model, so a default run neither touches the
        // agent's capability surface nor reads its property bag.
        var answerSampling = AnswerSamplingCoordinator.Create(options);

        // Time-grounding is refused rather than approximated. Falling back to text injection would
        // answer temporal questions from the very scaffolding this mode removes, and would report a
        // score for a measurement that never happened.
        ITimestampedHistoryInjectableAgent? timestampedAgent = null;
        TemporalGroundingAccumulator? grounding = null;
        if (options.TemporalGrounding != TemporalGroundingMode.None)
        {
            timestampedAgent = agent as ITimestampedHistoryInjectableAgent
                ?? throw new ArgumentException(
                    $"TemporalGrounding is {options.TemporalGrounding}, which delivers session dates " +
                    $"as real timestamps, but agent '{agent.Name}' does not implement " +
                    $"ITimestampedHistoryInjectableAgent. There is no text fallback for this mode: " +
                    $"the dates the fallback would use are the ones the mode exists to take away.",
                    nameof(agent));
            grounding = new TemporalGroundingAccumulator(options.TemporalGrounding);
        }
        var totalStopwatch = Stopwatch.StartNew();
        var questionResults = new List<QuestionResult>();

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

            // Applied after the reset, per question: an agent that clears state on reset would
            // otherwise be configured once and answer every later question unpinned.
            var samplingAttachment = answerSampling?.Configure(agent);
            if (samplingAttachment is { } attachment && i == 0 &&
                (attachment.Temperature == AnswerSamplingDisposition.NotSupportedByAgent ||
                 attachment.Seed == AnswerSamplingDisposition.NotSupportedByAgent))
            {
                _logger.LogWarning(
                    "Answer sampling was requested but {AgentName} does not implement " +
                    "IAnswerSamplingConfigurableAgent — the answer model runs at the provider " +
                    "default and the run is not pinned. See ExternalBenchmarkResult.AnswerSampling.",
                    agent.Name);
            }

            // Inject history (0 LLM calls) or fall back to text blob prepended to query
            string? textBlobPrefix = null;
            var injectionMode = options.HistoryInjectionMode;

            var useStructured = injectionMode switch
            {
                HistoryInjectionMode.StructuredChatHistory => true,
                HistoryInjectionMode.TextBlob => false,
                _ => agent is IHistoryInjectableAgent // Auto: use structured if available
            };

            if (timestampedAgent is not null)
            {
                var timestampedHistory = LongMemEvalHistoryFormatter.FormatTimestamped(entry, options);
                timestampedAgent.InjectTimestampedConversationHistory(timestampedHistory);
                grounding!.Observe(entry, timestampedHistory);
            }
            else if (useStructured && agent is IHistoryInjectableAgent injectable)
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
            // Under TimestampsOnly the query time arrives as TimestampedConversationHistory.QueryTime,
            // so printing it here would hand back the crutch that mode removes.
            if (textBlobPrefix == null && !string.IsNullOrEmpty(entry.QuestionDate) &&
                options.TemporalGrounding != TemporalGroundingMode.TimestampsOnly)
            {
                queryPrompt = $"Current Date: {entry.QuestionDate}\n\n{queryPrompt}";
            }

            AgentResponse response;
            try
            {
                response = await agent.InvokeAsync(queryPrompt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                qStopwatch.Stop();
                var safeCode = ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                    ? "content_filter"
                    // A provider that refuses the requested sampling gets its own code rather than a
                    // generic agent error: AgentEval never retries without the parameter, because a
                    // silent downgrade would produce a run that looks pinned and is not.
                    : samplingAttachment is not null && AnswerSamplingCoordinator.IsSamplingRejection(ex)
                        ? "answer_sampling_rejected"
                        : "agent_error";

                questionResults.Add(new QuestionResult
                {
                    QuestionId = entry.QuestionId,
                    QuestionType = entry.QuestionType,
                    Question = entry.Question,
                    GoldAnswer = entry.Answer,
                    AgentResponse = $"[{safeCode.ToUpperInvariant()}]",
                    Correct = null,
                    RawScore = null,
                    ExecutionStatus = QuestionExecutionStatus.AgentError,
                    JudgeStatus = null,
                    IsAbstention = entry.IsAbstention,
                    AgentLlmCallCount = 1,
                    JudgeLlmCallCount = 0,
                    JudgePrimaryLlmCallCount = 0,
                    JudgeRetryLlmCallCount = 0,
                    JudgeAttemptsUsed = 0,
                    AnswerSampling = samplingAttachment is { } failedAttachment
                        ? answerSampling!.Fail(failedAttachment, ex)
                        : null,
                    SafeFailureCode = safeCode,
                    JudgeExplanation = "Agent execution did not complete.",
                    Duration = qStopwatch.Elapsed
                });

                _logger.LogWarning(
                    "[{Index}/{Total}] {Type,-30} {Error} — {QuestionId}  ({Elapsed:F1}s)",
                    i + 1, entries.Count, entry.QuestionType, safeCode, entry.QuestionId, qStopwatch.Elapsed.TotalSeconds);
                continue;
            }
            // Evidence capture occurs only after the normal agent has answered.
            var evidenceCapture = LongMemEvalEvidenceCapture.Capture(response, entry, options);

            // Judge only after the agent has completed. Judge failures retain their own
            // typed status and can never be misclassified as agent failures.
            var question = new ExternalBenchmarkQuestion
            {
                QuestionId = entry.QuestionId,
                QuestionType = entry.QuestionType,
                Question = entry.Question,
                GoldAnswer = entry.Answer,
                QuestionDate = entry.QuestionDate,
                IsAbstention = entry.IsAbstention
            };

            var judgment = await judge.JudgeAsync(response.Text, question, options, ct);

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
                ExecutionStatus = QuestionExecutionStatus.Completed,
                JudgeStatus = judgment.Status,
                IsAbstention = entry.IsAbstention,
                AgentLlmCallCount = 1,
                JudgeLlmCallCount = judgment.LlmCallCount,
                JudgePrimaryLlmCallCount = judgment.PrimaryLlmCallCount,
                JudgeRetryLlmCallCount = judgment.RetryLlmCallCount,
                JudgeAttemptsUsed = judgment.AttemptsUsed,
                JudgeSystemFingerprint = judgment.SystemFingerprint,
                // Reading the agent's property bag is itself an observation of the agent, and with
                // provenance off AgentEval must not touch it at all — see
                // LongMemEvalEvidenceRunnerIntegrationTests.RunAsync_EvidenceCaptureNone_DoesNotInspectOrSerializeEvidence.
                AgentSystemFingerprint = options.RunProvenanceMode != RunProvenanceMode.None
                    ? ProviderFingerprint.FromAgentResponse(response.AdditionalProperties)
                    : null,
                JudgeTokensUsed = judgment.TokensUsed,
                AnswerSampling = samplingAttachment is { } completedAttachment
                    ? answerSampling!.Complete(completedAttachment, response)
                    : null,
                SafeFailureCode = judgment.SafeFailureCode,
                JudgeExplanation = judgment.Explanation,
                // Carried through so a stored run is diagnosable without re-running the question. All
                // four are null unless the caller opted in, so default output is unchanged.
                JudgeRawResponse = judgment.RawResponse,
                JudgeReasoning = judgment.Reasoning,
                JudgePredicateResults = judgment.PredicateResults,
                JudgePredicateCombinationRule = judgment.PredicateCombinationRule,
                Evidence = evidenceCapture.Envelope,
                EvidenceDiagnostics = evidenceCapture.Diagnostics,
                Duration = qStopwatch.Elapsed
            });

            var correctLabel = judgment.Status switch
            {
                JudgeOutcomeStatus.Yes => "CORRECT",
                JudgeOutcomeStatus.No => "WRONG",
                _ => $"INCONCLUSIVE ({judgment.Status})"
            };
            _logger.LogInformation(
                "[{Index}/{Total}] {Type,-30} {Correct}  ({Elapsed:F1}s)",
                i + 1, entries.Count, entry.QuestionType,
                correctLabel, qStopwatch.Elapsed.TotalSeconds);
        }

        totalStopwatch.Stop();

        // 3. Aggregate results
        return AggregateResults(
            questionResults, options, totalStopwatch.Elapsed, executionLabel, dataset, oracleProjection,
            grounding?.Build());
    }

    /// <summary>
    /// A loaded dataset together with what it was loaded from, so a run can report the file it
    /// sampled and how many questions that file held before sampling.
    /// </summary>
    private readonly record struct LoadedDataset(
        IReadOnlyList<LongMemEvalEntry> Entries,
        string ResolvedPath,
        int TotalQuestionsInFile);

    private LoadedDataset LoadEntries(ExternalBenchmarkOptions options)
    {
        // v0.10.1-beta: no embedded fallback any more. Resolution order:
        // explicit options.DatasetPath -> runner-baked _datasetPath -> LONGMEMEVAL_DATASET_PATH env var
        // -> canonical local path under workspace root -> throw LongMemEvalDatasetNotFoundException.
        var explicitPath = options.DatasetPath ?? _datasetPath;
        var resolved = LongMemEvalDataLoader.ResolveDatasetPath(explicitPath);
        if (resolved == null)
            throw LongMemEvalDatasetNotFoundException.ForResolutionFailure();

        var entries = LongMemEvalDataLoader.LoadFromFile(resolved, options, out var totalInFile);
        return new LoadedDataset(entries, resolved, totalInFile);
    }

    private ExternalBenchmarkResult AggregateResults(
        List<QuestionResult> questionResults,
        ExternalBenchmarkOptions options,
        TimeSpan duration,
        string? executionLabel,
        LoadedDataset? dataset = null,
        OracleProjectionReport? oracleProjection = null,
        TemporalGroundingReport? temporalGrounding = null)
    {
        bool IsScored(QuestionResult question) =>
            question.Correct.HasValue ||
            (options.JudgeFailurePolicy == JudgeFailurePolicy.RetryThenIncorrect &&
             question.ExecutionStatus == QuestionExecutionStatus.Completed &&
             question.JudgeStatus is not JudgeOutcomeStatus.Yes and
                 not JudgeOutcomeStatus.No);

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
                    CorrectQuestions = g.Count(q => q.Correct is true),
                    ScoredQuestions = g.Count(IsScored),
                    InconclusiveQuestions = g.Count(q =>
                        q.ExecutionStatus == QuestionExecutionStatus.Completed &&
                        !q.Correct.HasValue),
                    AgentFailureQuestions = g.Count(q =>
                        q.ExecutionStatus == QuestionExecutionStatus.AgentError),
                    Duration = TimeSpan.FromTicks(g.Sum(q => q.Duration.Ticks))
                });

        // Micro-average (overall)
        var totalCorrect = questionResults.Count(q => q.Correct is true);
        var totalIncorrect = questionResults.Count(q => q.Correct is false);
        var totalScored = questionResults.Count(IsScored);
        double? overallAccuracy = totalScored > 0
            ? (double)totalCorrect / totalScored * 100
            : null;

        // Macro-average only includes types with a defined denominator.
        var scoredTypeAccuracies = perType.Values
            .Select(t => t.Accuracy)
            .Where(a => a.HasValue)
            .Select(a => a!.Value)
            .ToList();
        double? taskAveraged = scoredTypeAccuracies.Count > 0
            ? scoredTypeAccuracies.Average()
            : null;
        var agentCompleted = questionResults.Count(q =>
            q.ExecutionStatus == QuestionExecutionStatus.Completed);
        var inconclusive = questionResults.Count(q =>
            q.ExecutionStatus == QuestionExecutionStatus.Completed && !q.Correct.HasValue);
        var agentFailures = questionResults.Count - agentCompleted;
        var totalLlmCalls = questionResults.Sum(q =>
            q.AgentLlmCallCount + q.JudgeLlmCallCount);
        var totalJudgeRetryCalls = questionResults.Sum(q => q.JudgeRetryLlmCallCount);

        // Counted from questionResults — the same list the accuracy denominators above are counted
        // from — so a composition and a denominator can never disagree about what ran.
        var composition = new SampleComposition
        {
            TotalQuestions = questionResults.Count,
            AbstentionQuestions = questionResults.Count(q => q.IsAbstention),
            NonAbstentionQuestions = questionResults.Count(q => !q.IsAbstention),
            ByQuestionType = questionResults
                .GroupBy(q => q.QuestionType)
                .ToDictionary(
                    g => g.Key,
                    g => new QuestionTypeComposition
                    {
                        TotalQuestions = g.Count(),
                        AbstentionQuestions = g.Count(q => q.IsAbstention)
                    }),
            RequestedQuestionTypes = options.IncludeQuestionTypes is { Count: > 0 } requested
                ? requested.ToArray()
                : null,
            RequestedAbstentionPolicy = options.AbstentionPolicy,
            RequestedAbstentionProportion = options.AbstentionTargetProportion
        };

        // Null unless the caller requested answer sampling, so the default result shape is unchanged.
        var answerSamplingReport = AnswerSamplingReport.From(
            options,
            questionResults.Select(q => q.AnswerSampling).ToList());

        var judgeFingerprints = questionResults
            .Select(q => q.JudgeSystemFingerprint)
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        var provenance = LongMemEvalProvenance.Capture(
            options.RunProvenanceMode,
            dataset?.ResolvedPath,
            dataset?.TotalQuestionsInFile,
            // Only meaningful when the ids came from a real load; a caller-supplied entry list has no
            // dataset file behind it.
            dataset is null ? null : questionResults.Select(q => q.QuestionId));

        var benchmarkName = options.DatasetMode != null
            ? $"LongMemEval-{options.DatasetMode} {questionResults.Count}q"
            : $"LongMemEval {questionResults.Count}q";
        if (!string.IsNullOrWhiteSpace(executionLabel))
            benchmarkName += $" ({executionLabel})";

        return new ExternalBenchmarkResult
        {
            BenchmarkId = BenchmarkId,
            BenchmarkName = benchmarkName,
            OverallAccuracy = overallAccuracy,
            TaskAveragedAccuracy = taskAveraged,
            PerTypeResults = perType,
            QuestionResults = questionResults,
            SelectedQuestions = questionResults.Count,
            AgentCompletedQuestions = agentCompleted,
            ScoredQuestions = totalScored,
            CorrectQuestions = totalCorrect,
            IncorrectQuestions = totalIncorrect,
            InconclusiveQuestions = inconclusive,
            AgentFailureQuestions = agentFailures,
            JudgeFailureRate = agentCompleted > 0 ? (double)inconclusive / agentCompleted : null,
            ScoredTypeCount = scoredTypeAccuracies.Count,
            Duration = duration,
            TotalLlmCalls = totalLlmCalls,
            TotalJudgeRetryLlmCalls = totalJudgeRetryCalls,
            Composition = composition,
            AnswerSampling = answerSamplingReport,
            OracleProjection = oracleProjection,
            TemporalGrounding = temporalGrounding,
            Provenance = provenance,
            JudgeSystemFingerprints = judgeFingerprints.Length > 0 ? judgeFingerprints : null,
            Options = options
        };
    }
}
