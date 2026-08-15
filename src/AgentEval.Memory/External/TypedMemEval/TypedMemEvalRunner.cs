// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Runs one TypedMemEval vertical against an agent, or through the oracle arm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Citation rule.</b> Cite results as "TypedMemEval-&lt;Vertical&gt; v1 (AgentEval)".
/// TypedMemEval results are <b>not</b> LongMemEval results and must never be presented as, summed
/// with, or averaged with LongMemEval numbers. The twelve Prospective questions seeded from the
/// time-grounded probe exist in both <c>agenteval-timegrounded-v1</c> and TypedMemEval-Prospective
/// v1; a report that runs both must not double-count them.
/// </para>
/// <para>
/// A separate class rather than a partial of the LongMemEval runner, deliberately: hanging five
/// corpora off that name would be the benchmark-name substitution this family exists to prevent.
/// The shared machinery underneath is reused wholesale — history formatting, timestamped injection,
/// answer-sampling coordination, evidence capture, the oracle projector, provenance — because that
/// pipeline is hardened and re-proving it would buy nothing.
/// </para>
/// </remarks>
public sealed class TypedMemEvalRunner
{
    private readonly IChatClient _judgeClient;
    private readonly ILogger<TypedMemEvalRunner> _logger;

    /// <summary>Creates a runner over the judge's chat client.</summary>
    public TypedMemEvalRunner(IChatClient judgeClient, ILogger<TypedMemEvalRunner>? logger = null)
    {
        _judgeClient = judgeClient ?? throw new ArgumentNullException(nameof(judgeClient));
        _logger = logger ?? NullLogger<TypedMemEvalRunner>.Instance;
    }

    /// <summary>
    /// The vertical the benchmark registry bound this runner to, so a caller who obtained it by
    /// preset name does not have to name the vertical twice. Null when constructed directly.
    /// </summary>
    public TypedMemEvalVertical? DefaultVertical { get; init; }

    /// <summary>Runs <see cref="DefaultVertical"/> against the agent under test.</summary>
    /// <exception cref="InvalidOperationException">
    /// When the runner was constructed directly and so has no bound vertical. The verticals
    /// measure different mechanisms, so there is no sensible default to fall back to.
    /// </exception>
    public Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        TypedMemEvalOptions? options = null,
        CancellationToken ct = default)
        => DefaultVertical is { } vertical
            ? RunAsync(agent, vertical, options, ct)
            : throw new InvalidOperationException(
                "TypedMemEvalRunner has no DefaultVertical. Either obtain the runner from the " +
                "benchmark registry by preset name, or call RunAsync(agent, vertical). The five " +
                "verticals measure different mechanisms, so there is no default to pick.");

    /// <summary>Runs one vertical against the agent under test.</summary>
    /// <param name="agent">The agent under test.</param>
    /// <param name="vertical">Which mechanism to measure.</param>
    /// <param name="options">Null runs the vertical's own defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return await RunCoreAsync(agent, vertical, options, oracleOptions: null, executionLabel: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one vertical through the oracle arm — perfect retrieval, so the number is the ceiling
    /// the answer model can reach when the evidence is genuinely present.
    /// </summary>
    /// <remarks>
    /// Uses the same projector and reader the LongMemEval oracle arm uses, so a consuming project's
    /// ceiling and this one are the same number from the same knobs. That is why
    /// <see cref="LongMemEvalOracleOptions"/> keeps its name here: it is shipped, frozen API, and
    /// the point of reusing it is that the two ceilings are comparable. It never appears in the
    /// result JSON — only <see cref="OracleProjectionReport"/>'s numbers do.
    /// </remarks>
    public async Task<ExternalBenchmarkResult> RunOracleAsync(
        IChatClient answerClient,
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? options = null,
        LongMemEvalOracleOptions? oracleOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answerClient);
        oracleOptions ??= LongMemEvalOracleOptions.GoldOnly;
        oracleOptions.Validate();

        return await RunCoreAsync(
                new LongMemEvalOracleReader(answerClient), vertical, options, oracleOptions,
                executionLabel: "oracle", ct)
            .ConfigureAwait(false);
    }

    private async Task<ExternalBenchmarkResult> RunCoreAsync(
        IEvaluableAgent agent,
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? facade,
        LongMemEvalOracleOptions? oracleOptions,
        string? executionLabel,
        CancellationToken ct)
    {
        var descriptor = TypedMemEvalVerticals.For(vertical);
        facade ??= new TypedMemEvalOptions();
        facade.Validate();

        var options = facade.ToExternalOptions(descriptor);
        options.Validate();

        var corpusJson = TypedMemEvalCorpus.ReadJson(vertical);
        var extensions = TypedMemEvalExtensions.Parse(corpusJson);
        var entries = LongMemEvalDataLoader.LoadFromJson(corpusJson, options, out var totalInCorpus);

        // Refused rather than approximated, before any provider call: falling back to text
        // injection would answer the vertical's questions from the very date scaffolding the mode
        // exists to remove, and would report a score for a measurement that never happened.
        ITimestampedHistoryInjectableAgent? timestampedAgent = null;
        if (options.TemporalGrounding != TemporalGroundingMode.None)
        {
            timestampedAgent = agent as ITimestampedHistoryInjectableAgent
                ?? throw new ArgumentException(
                    $"{descriptor.DisplayName} runs under {options.TemporalGrounding} grounding, which " +
                    $"delivers session dates as real timestamps, but agent '{agent.Name}' does not " +
                    $"implement ITimestampedHistoryInjectableAgent. There is no text fallback: the dates " +
                    $"the fallback would use are the ones this vertical exists to take away.",
                    nameof(agent));
        }

        OracleProjectionReport? projectionReport = null;
        if (oracleOptions is not null)
        {
            var projections = entries
                .Select(entry => LongMemEvalOracleProjector.Project(entry, oracleOptions, options.RandomSeed))
                .ToArray();
            entries = projections.Select(p => p.Entry).ToArray();
            projectionReport = OracleProjectionReport.From(
                oracleOptions, projections.Select(p => p.Realised).ToArray());
        }

        // A duration answer is recoverable only from session timestamps. Under a timestamp-free
        // mode those questions are unanswerable by construction, so they are reported unrun with a
        // reason rather than counted as failures of the system under test.
        var timestampsSuppressed = descriptor.RequiresTimestamps &&
                                   !options.IncludeTimestamps &&
                                   options.TemporalGrounding == TemporalGroundingMode.None;

        var judge = new TypedMemEvalJudge(_judgeClient, NullLogger<TypedMemEvalJudge>.Instance);
        var answerSampling = AnswerSamplingCoordinator.Create(options);
        var results = new List<QuestionResult>(entries.Count);
        var details = new Dictionary<string, TypedMemEvalQuestionDetail>(StringComparer.Ordinal);
        var unrunReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var runStopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "{Benchmark}: {Count} questions from {CorpusId} (K_ref={KRef})",
            descriptor.DisplayName, entries.Count, descriptor.CorpusId,
            TypedMemEvalCorpus.ReferenceBudgetSessions);

        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            var extension = extensions[entry.QuestionId];
            var questionStopwatch = Stopwatch.StartNew();

            if (timestampsSuppressed && extension.Shape == "duration")
            {
                const string reason =
                    "duration answers derive from session timestamps, which this run did not deliver";
                unrunReasons[entry.QuestionId] = reason;
                details[entry.QuestionId] = Detail(
                    TypedMemEvalOutcome.Unrun, extension, TypedMemEvalCoverage.Unobserved, null);
                results.Add(Skipped(entry, extension, reason, questionStopwatch.Elapsed));
                continue;
            }

            if (agent is ISessionResettableAgent resettable)
                await resettable.ResetSessionAsync(ct).ConfigureAwait(false);

            // Applied after the reset, per question: an agent that clears state on reset would
            // otherwise be configured once and answer every later question unpinned.
            var attachment = answerSampling?.Configure(agent);
            if (attachment is { } first && i == 0 &&
                (first.Temperature == AnswerSamplingDisposition.NotSupportedByAgent ||
                 first.Seed == AnswerSamplingDisposition.NotSupportedByAgent))
            {
                _logger.LogWarning(
                    "Answer sampling was requested but {AgentName} does not implement " +
                    "IAnswerSamplingConfigurableAgent — the answer model runs at the provider " +
                    "default and the run is not pinned. See ExternalBenchmarkResult.AnswerSampling.",
                    agent.Name);
            }

            var textBlobPrefix = InjectHistory(agent, timestampedAgent, entry, options);
            var prompt = BuildPrompt(entry, textBlobPrefix, options);

            AgentResponse response;
            try
            {
                response = await agent.InvokeAsync(prompt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                questionStopwatch.Stop();
                var outcome = attachment is { } failed ? answerSampling!.Fail(failed, ex) : null;
                // Derived from the stored disposition so the code and the record cannot disagree,
                // and so a provider that refused AgentEval's own sampling request gets its own code
                // rather than a generic agent error. AgentEval never retries without the parameter:
                // a silent downgrade produces a run that looks pinned and is not.
                var safeCode = ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                    ? "content_filter"
                    : outcome is not null && outcome.WasRejectedByProvider
                        ? "answer_sampling_rejected"
                        : "agent_error";

                details[entry.QuestionId] = Detail(
                    TypedMemEvalOutcome.Inconclusive, extension, TypedMemEvalCoverage.Unobserved, null);
                results.Add(new QuestionResult
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
                    AnswerSampling = outcome,
                    SafeFailureCode = safeCode,
                    JudgeExplanation = "Agent execution did not complete.",
                    TypedOutcome = details[entry.QuestionId],
                    Duration = questionStopwatch.Elapsed
                });
                _logger.LogWarning(
                    "[{Index}/{Total}] {Shape,-24} {Error} — {QuestionId}",
                    i + 1, entries.Count, extension.Shape, safeCode, entry.QuestionId);
                continue;
            }

            var evidence = LongMemEvalEvidenceCapture.Capture(response, entry, options);
            var coverage = TypedMemEvalCoverage.Compute(
                evidence.Envelope, extension, GoldTexts(entry, extension));

            var judgment = await judge
                .JudgeAsync(response.Text, entry.Question, entry.Answer, vertical, extension, options, ct)
                .ConfigureAwait(false);

            var detail = Detail(
                judgment.Outcome ?? TypedMemEvalOutcome.Inconclusive, extension, coverage, judgment);
            details[entry.QuestionId] = detail;
            questionStopwatch.Stop();

            results.Add(new QuestionResult
            {
                QuestionId = entry.QuestionId,
                QuestionType = entry.QuestionType,
                Question = entry.Question,
                GoldAnswer = entry.Answer,
                AgentResponse = response.Text,
                // The binary fields stay populated so generic tooling keeps working. They are
                // compatibility surface, not a TypedMemEval score — the typed vector is.
                Correct = judgment.Outcome switch
                {
                    TypedMemEvalOutcome.Correct => true,
                    null => null,
                    _ => false
                },
                RawScore = judgment.Outcome switch
                {
                    TypedMemEvalOutcome.Correct => 100,
                    null => null,
                    _ => 0
                },
                ExecutionStatus = QuestionExecutionStatus.Completed,
                JudgeStatus = judgment.Outcome switch
                {
                    TypedMemEvalOutcome.Correct => JudgeOutcomeStatus.Yes,
                    null => JudgeOutcomeStatus.Invalid,
                    _ => JudgeOutcomeStatus.No
                },
                IsAbstention = entry.IsAbstention,
                AgentLlmCallCount = 1,
                JudgeLlmCallCount = judgment.LlmCallCount,
                JudgePrimaryLlmCallCount = judgment.PrimaryLlmCallCount,
                JudgeRetryLlmCallCount = judgment.RetryLlmCallCount,
                JudgeAttemptsUsed = judgment.AttemptsUsed,
                JudgeSystemFingerprint = judgment.SystemFingerprint,
                AgentSystemFingerprint = ProviderFingerprint.FromAgentResponse(response.AdditionalProperties),
                JudgeTokensUsed = judgment.TokensUsed,
                AnswerSampling = attachment is { } done ? answerSampling!.Complete(done, response) : null,
                SafeFailureCode = judgment.SafeFailureCode,
                JudgeExplanation = judgment.Reasoning ?? $"Judge outcome: {judgment.Outcome}",
                JudgeRawResponse = judgment.RawResponse,
                JudgeReasoning = judgment.Reasoning,
                Evidence = evidence.Envelope,
                EvidenceDiagnostics = evidence.Diagnostics,
                TypedOutcome = detail,
                Duration = questionStopwatch.Elapsed
            });

            _logger.LogInformation(
                "[{Index}/{Total}] {Shape,-24} {Outcome}",
                i + 1, entries.Count, extension.Shape, detail.Outcome);
        }

        runStopwatch.Stop();

        return TypedMemEvalReportBuilder.Build(
            descriptor, options, facade, results, details, unrunReasons,
            runStopwatch.Elapsed, executionLabel, projectionReport, totalInCorpus,
            TypedMemEvalCorpus.Sha256(vertical),
            TypedMemEvalCorpus.CalibratedFloorMean(vertical));
    }

    private string? InjectHistory(
        IEvaluableAgent agent,
        ITimestampedHistoryInjectableAgent? timestampedAgent,
        LongMemEvalEntry entry,
        ExternalBenchmarkOptions options)
    {
        if (timestampedAgent is not null)
        {
            timestampedAgent.InjectTimestampedConversationHistory(
                LongMemEvalHistoryFormatter.FormatTimestamped(entry, options));
            return null;
        }

        var useStructured = options.HistoryInjectionMode switch
        {
            HistoryInjectionMode.StructuredChatHistory => true,
            HistoryInjectionMode.TextBlob => false,
            _ => agent is IHistoryInjectableAgent
        };

        if (useStructured && agent is IHistoryInjectableAgent injectable)
        {
            injectable.InjectConversationHistory(LongMemEvalHistoryFormatter.Format(entry, options));
            return null;
        }

        if (options.HistoryInjectionMode == HistoryInjectionMode.StructuredChatHistory)
        {
            _logger.LogWarning(
                "HistoryInjectionMode is StructuredChatHistory but the agent does not implement " +
                "IHistoryInjectableAgent — falling back to text blob for {QuestionId}",
                entry.QuestionId);
        }
        return LongMemEvalHistoryFormatter.FormatAsTextBlob(entry, options);
    }

    private static string BuildPrompt(
        LongMemEvalEntry entry, string? textBlobPrefix, ExternalBenchmarkOptions options)
    {
        var prompt = textBlobPrefix is not null
            ? $"{textBlobPrefix}\nQuestion: {entry.Question}\nAnswer:"
            : entry.Question;

        // Under TimestampsOnly the query time arrives through the typed channel, so printing it
        // here would hand back the crutch that mode exists to remove.
        if (textBlobPrefix is null &&
            !string.IsNullOrEmpty(entry.QuestionDate) &&
            options.TemporalGrounding != TemporalGroundingMode.TimestampsOnly)
        {
            prompt = $"Current Date: {entry.QuestionDate}\n\n{prompt}";
        }
        return prompt;
    }

    /// <summary>
    /// The answer-bearing text of each gold session, used only for the optional content-level
    /// evidence upgrade.
    /// </summary>
    private static Dictionary<int, string> GoldTexts(
        LongMemEvalEntry entry, TypedMemEvalExtension extension)
    {
        var texts = new Dictionary<int, string>();
        var sessions = entry.HaystackSessions;
        if (sessions is null)
            return texts;

        foreach (var index in extension.GoldSessionIndices)
        {
            if (index < 0 || index >= sessions.Count)
                continue;
            var turn = sessions[index].FirstOrDefault(t => t.HasAnswer == true);
            if (turn is not null)
                texts[index] = turn.Content;
        }
        return texts;
    }

    private static TypedMemEvalQuestionDetail Detail(
        TypedMemEvalOutcome outcome,
        TypedMemEvalExtension extension,
        TypedMemEvalCoverage.Result coverage,
        TypedMemEvalJudgment? judgment)
    {
        // The three Forgetting sub-counts are derived here, from the shape the corpus declares and
        // the flag the judge sets, rather than inferred from the outcome alone. Inferring them
        // would collapse "asserted the superseded value as current" into plain Wrong, and that
        // distinction is the dangerous-error class the vertical exists to surface.
        var stale = extension.Shape == "invalidated" && judgment?.StaleValueAsserted == true;
        // Keyed on what the answer claimed, not on its outcome. A still-valid control answered with
        // the wrong value is an ordinary miss; one answered "I no longer have that" is the system
        // forgetting something it should still hold, and only the flag can tell them apart.
        var overForgetting = extension.Shape == "still-valid" &&
                             judgment?.ClaimedNoLongerKnown == true;
        var misattributed = extension.Shape == "never-known" &&
                            judgment?.ClaimedNoLongerKnown == true;

        double? pairwise = null;
        if (judgment?.OrderedPairsTotal is { } total && total > 0 &&
            judgment.OrderedPairsCorrect is { } correct)
        {
            pairwise = (double)correct / total;
        }

        return new TypedMemEvalQuestionDetail
        {
            Outcome = outcome,
            Attribution = coverage.Attribution,
            AttributionLevel = coverage.Level,
            RealisedGoldCoverage = coverage.Coverage,
            CoverageSource = coverage.Source,
            ComponentCoverage = coverage.Components,
            Shape = extension.Shape,
            PairId = extension.PairId,
            Arm = extension.Arm,
            DistanceSessions = extension.DistanceSessions,
            SeededFrom = extension.SeededFrom,
            IsStaleRecall = stale,
            IsOverForgetting = overForgetting,
            IsMisattributedForgetting = misattributed,
            PairwiseOrderAccuracy = pairwise
        };
    }

    private static QuestionResult Skipped(
        LongMemEvalEntry entry, TypedMemEvalExtension extension, string reason, TimeSpan elapsed)
        => new()
        {
            QuestionId = entry.QuestionId,
            QuestionType = entry.QuestionType,
            Question = entry.Question,
            GoldAnswer = entry.Answer,
            AgentResponse = "[UNRUN]",
            Correct = null,
            RawScore = null,
            ExecutionStatus = QuestionExecutionStatus.AgentError,
            JudgeStatus = null,
            IsAbstention = entry.IsAbstention,
            AgentLlmCallCount = 0,
            SafeFailureCode = "unrun_requires_timestamps",
            JudgeExplanation = reason,
            Duration = elapsed
        };
}
