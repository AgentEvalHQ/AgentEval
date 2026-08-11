// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// LLM-based judge for LongMemEval that selects type-specific prompts matching the official benchmark.
/// Each question type has its own tolerance rules (temporal off-by-one, knowledge-update old+new, etc.).
/// </summary>
public class LongMemEvalJudge : IExternalBenchmarkJudge
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<LongMemEvalJudge> _logger;

    // Remembered per judge instance: once a provider has rejected a response-format constraint there is
    // no value in paying for that rejection on every subsequent question. Mirrors the temperature
    // memoisation in LLMJudgeEvaluator.
    private bool _jsonSchemaUnsupported;
    private bool _jsonModeUnsupported;

    public LongMemEvalJudge(IChatClient chatClient, ILogger<LongMemEvalJudge> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Judges a response using the type-specific prompt from the official LongMemEval evaluation.
    /// Returns binary yes/no matching the official scoring methodology.
    /// </summary>
    public Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        CancellationToken ct = default)
        => JudgeAsync(agentResponse, question, new ExternalBenchmarkOptions(), ct);

    /// <summary>Judges a response with explicit bounded failure and diagnostic options.</summary>
    public async Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentResponse);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return options.JudgeDecompositionMode == JudgeDecompositionMode.PerPredicate
            ? await JudgePerPredicateAsync(agentResponse, question, options, ct).ConfigureAwait(false)
            : await JudgeWholeQuestionAsync(agentResponse, question, options, ct).ConfigureAwait(false);
    }

    private async Task<ExternalJudgmentResult> JudgeWholeQuestionAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        ExternalBenchmarkOptions options,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(SelectPrompt(question, agentResponse), options);
        var attempt = await RunJudgeLoopAsync(prompt, question, options, ct).ConfigureAwait(false);

        return CreateResult(
            attempt.Status,
            attempt.Raw,
            attempt.ProviderCalls,
            attempt.Tokens,
            options,
            attempt.FailureCode,
            attempt.Reasoning);
    }

    private async Task<ExternalJudgmentResult> JudgePerPredicateAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        ExternalBenchmarkOptions options,
        CancellationToken ct)
    {
        // Abstention questions are not decomposable. Their gold "answer" is an explanation of why the
        // question is unanswerable, and the official judge asks whether the model RECOGNISED it as
        // unanswerable — a different question from "does the response support this fact?". Splitting the
        // explanation into predicates and asking the per-predicate question would score something the
        // benchmark never asked, so these are always judged whole.
        if (question.IsAbstention)
            return await JudgeWholeQuestionAsync(agentResponse, question, options, ct).ConfigureAwait(false);

        var predicates = LongMemEvalPredicateExtractor.Extract(question.GoldAnswer);
        var predicateResults = new List<JudgePredicateResult>(predicates.Count);
        var totalTokens = 0;
        var totalCalls = 0;

        for (var index = 0; index < predicates.Count; index++)
        {
            var prompt = BuildPrompt(
                LongMemEvalJudgePrompts.Predicate(question.Question, predicates[index], agentResponse),
                options);

            var attempt = await RunJudgeLoopAsync(prompt, question, options, ct).ConfigureAwait(false);

            totalTokens += attempt.Tokens;
            totalCalls += attempt.ProviderCalls;
            predicateResults.Add(new JudgePredicateResult
            {
                Index = index,
                Predicate = predicates[index],
                Status = attempt.Status,
                SafeFailureCode = attempt.FailureCode,
                Reasoning = attempt.Reasoning,
                LlmCallCount = attempt.ProviderCalls,
                TokensUsed = attempt.Tokens
            });
        }

        var (status, failureCode) = Combine(predicateResults, options.PredicateCombinationRule);

        return CreateResult(
            status,
            // The aggregate has no single raw response — each predicate carries its own reasoning.
            raw: null,
            totalCalls,
            totalTokens,
            options,
            failureCode,
            reasoning: null,
            predicateResults,
            options.PredicateCombinationRule);
    }

    /// <summary>
    /// Combines per-predicate outcomes. The rule is applied here, in code, and echoed onto the result so
    /// a stored judgment records which rule produced it.
    /// </summary>
    internal static (JudgeOutcomeStatus Status, string? FailureCode) Combine(
        IReadOnlyList<JudgePredicateResult> predicates,
        PredicateCombinationRule rule)
    {
        if (predicates.Count == 0)
            return (JudgeOutcomeStatus.Invalid, "predicate_none_extracted");

        var yes = predicates.Count(p => p.Status == JudgeOutcomeStatus.Yes);
        var no = predicates.Count(p => p.Status == JudgeOutcomeStatus.No);
        var unresolved = predicates.Count - yes - no;

        switch (rule)
        {
            case PredicateCombinationRule.AllMustHold:
                // A single definite No already violates all-must-hold, so the verdict is No even if
                // another predicate is unknown — the unknown cannot rescue it.
                if (no > 0)
                    return (JudgeOutcomeStatus.No, null);
                // No definite failure, but something is unknown: the question is unjudged, not correct.
                if (unresolved > 0)
                    return (JudgeOutcomeStatus.Invalid, "predicate_inconclusive");
                return (JudgeOutcomeStatus.Yes, null);

            case PredicateCombinationRule.Majority:
                var threshold = (predicates.Count / 2) + 1;
                if (yes >= threshold)
                    return (JudgeOutcomeStatus.Yes, null);
                // Even if every unknown resolved to Yes, the majority is unreachable.
                if (yes + unresolved < threshold)
                    return (JudgeOutcomeStatus.No, null);
                return (JudgeOutcomeStatus.Invalid, "predicate_inconclusive");

            default:
                return (JudgeOutcomeStatus.Invalid, "predicate_unknown_rule");
        }
    }

    private static string BuildPrompt(string freeTextPrompt, ExternalBenchmarkOptions options)
        => options.JudgeVerdictProtocol == JudgeVerdictProtocol.StructuredJson
            ? LongMemEvalJudgePrompts.ForStructuredOutput(freeTextPrompt)
            : freeTextPrompt;

    private readonly record struct JudgeAttempt(
        JudgeOutcomeStatus Status,
        string? Raw,
        string? Reasoning,
        string? FailureCode,
        int ProviderCalls,
        int Tokens);

    /// <summary>
    /// Runs one judge question to a conclusive outcome or to the retry bound. Provider calls are counted
    /// individually, including response-format fallback calls, so reported spend is what was actually
    /// paid for rather than the number of logical attempts.
    /// </summary>
    private async Task<JudgeAttempt> RunJudgeLoopAsync(
        string judgePrompt,
        ExternalBenchmarkQuestion question,
        ExternalBenchmarkOptions options,
        CancellationToken ct)
    {
        var totalTokens = 0;
        var providerCalls = 0;
        var attempts = 0;
        JudgeAttempt last = default;

        while (attempts <= options.MaxJudgeRetries)
        {
            attempts++;
            try
            {
                var response = await InvokeProviderAsync(judgePrompt, options, ct, callCount: () => providerCalls++)
                    .ConfigureAwait(false);

                totalTokens += (int)(response.Usage?.TotalTokenCount ?? 0);
                var finishReason = response.FinishReason?.Value;

                if (options.JudgeVerdictProtocol == JudgeVerdictProtocol.StructuredJson)
                {
                    var (status, reasoning, failureCode) =
                        LongMemEvalStructuredVerdict.Parse(response.Text, finishReason);
                    last = new JudgeAttempt(
                        status, response.Text, reasoning, failureCode, providerCalls, totalTokens);
                }
                else
                {
                    var status = ParseResponse(response.Text, finishReason);
                    last = new JudgeAttempt(
                        status, response.Text, null, FreeTextFailureCode(status, finishReason),
                        providerCalls, totalTokens);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var safeCode = ex is TimeoutException ? "timeout" : "provider_error";
                _logger.LogWarning(
                    "LongMemEval judge provider failure for question {QuestionId}: {FailureCode}",
                    question.QuestionId,
                    safeCode);
                last = new JudgeAttempt(
                    JudgeOutcomeStatus.ProviderError, null, null, safeCode, providerCalls, totalTokens);
            }

            if (last.Status is JudgeOutcomeStatus.Yes or JudgeOutcomeStatus.No)
                return last;

            if (options.JudgeFailurePolicy == JudgeFailurePolicy.FailRun)
                throw new LongMemEvalJudgeException(question.QuestionId, last.Status);
        }

        return last;
    }

    private static string? FreeTextFailureCode(JudgeOutcomeStatus status, string? finishReason)
        => finishReason?.ToLowerInvariant() switch
        {
            "content_filter" => "content_filtered",
            "length" => "invalid_finish_reason",
            _ when status == JudgeOutcomeStatus.Empty => "empty_response",
            _ when status == JudgeOutcomeStatus.Invalid => "invalid_response",
            _ => null
        };

    /// <summary>
    /// Issues one judge call. Under the structured protocol the provider's schema-constrained format is
    /// preferred, degrading to plain JSON mode and then to an unconstrained call — the prompt carries the
    /// JSON contract either way, so an unconstrained provider can still comply and the parser still
    /// refuses to guess if it does not.
    /// </summary>
    private async Task<ChatResponse> InvokeProviderAsync(
        string judgePrompt,
        ExternalBenchmarkOptions options,
        CancellationToken ct,
        Action callCount)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, judgePrompt) };

        if (options.JudgeVerdictProtocol != JudgeVerdictProtocol.StructuredJson)
        {
            callCount();
            return await _chatClient
                .GetResponseAsync(messages, BuildChatOptions(options, null), ct)
                .ConfigureAwait(false);
        }

        if (!_jsonSchemaUnsupported)
        {
            try
            {
                callCount();
                return await _chatClient.GetResponseAsync(
                    messages,
                    BuildChatOptions(options, ChatResponseFormat.ForJsonSchema(
                        LongMemEvalStructuredVerdict.Schema,
                        LongMemEvalStructuredVerdict.SchemaName,
                        "Closed-set LongMemEval judge verdict with separate reasoning.")),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsResponseFormatUnsupported(ex))
            {
                _jsonSchemaUnsupported = true;
                _logger.LogInformation(
                    "Judge provider rejected a JSON-schema response format; falling back to JSON mode.");
            }
        }

        if (!_jsonModeUnsupported)
        {
            try
            {
                callCount();
                return await _chatClient
                    .GetResponseAsync(messages, BuildChatOptions(options, ChatResponseFormat.Json), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsResponseFormatUnsupported(ex))
            {
                _jsonModeUnsupported = true;
                _logger.LogInformation(
                    "Judge provider rejected JSON mode; falling back to an unconstrained judge call.");
            }
        }

        callCount();
        return await _chatClient
            .GetResponseAsync(messages, BuildChatOptions(options, null), ct)
            .ConfigureAwait(false);
    }

    private static ChatOptions BuildChatOptions(
        ExternalBenchmarkOptions options,
        ChatResponseFormat? responseFormat)
        => new()
        {
            // Left null by default on purpose: an explicit temperature is rejected by reasoning-model
            // deployments. Set ExternalBenchmarkOptions.JudgeTemperature = 0 for a deterministic judge.
            Temperature = (float?)options.JudgeTemperature,
            MaxOutputTokens = options.JudgeMaxOutputTokens,
            ResponseFormat = responseFormat
        };

    // A provider that does not support the requested response format surfaces an HTTP 400
    // invalid_request_error naming the parameter. Recognise THAT and only that, so a genuine judge error
    // (network, timeout, overload) still propagates to the ProviderError path instead of being retried
    // as though it were a capability problem. Mirrors ChatClientEvaluator.IsResponseFormatUnsupported.
    private static bool IsResponseFormatUnsupported(Exception ex)
    {
        var m = ex.Message;
        bool namesFormat =
            m.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("response format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json_object", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json mode", StringComparison.OrdinalIgnoreCase)
            || m.Contains("structured output", StringComparison.OrdinalIgnoreCase);
        bool looksRejected =
            m.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || m.Contains("400", StringComparison.OrdinalIgnoreCase);
        return namesFormat && looksRejected;
    }

    internal static JudgeOutcomeStatus ParseResponse(string? raw, string? finishReason = null)
    {
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            return JudgeOutcomeStatus.Invalid;

        var text = raw?.Trim().TrimStart('\uFEFF').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return JudgeOutcomeStatus.Empty;
        if (text.Length > 16_384)
            return JudgeOutcomeStatus.Invalid;

        var tokenLength = 0;
        while (tokenLength < text.Length && char.IsLetter(text[tokenLength]))
            tokenLength++;
        if (tokenLength == 0)
            return JudgeOutcomeStatus.Invalid;

        var firstToken = text[..tokenLength];
        var status = firstToken.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? JudgeOutcomeStatus.Yes
            : firstToken.Equals("no", StringComparison.OrdinalIgnoreCase)
                ? JudgeOutcomeStatus.No
                : JudgeOutcomeStatus.Invalid;
        if (status == JudgeOutcomeStatus.Invalid)
            return status;

        var opposite = status == JudgeOutcomeStatus.Yes ? "no" : "yes";
        return ContainsWord(text[tokenLength..], opposite)
            ? JudgeOutcomeStatus.Invalid
            : status;
    }

    private static bool ContainsWord(string text, string expected)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetter(text[i]))
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                if (text.AsSpan(start, i - start).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
                start = -1;
            }
        }
        return false;
    }

    private static ExternalJudgmentResult CreateResult(
        JudgeOutcomeStatus status,
        string? raw,
        int providerCalls,
        int totalTokens,
        ExternalBenchmarkOptions options,
        string? safeFailureCode = null,
        string? reasoning = null,
        IReadOnlyList<JudgePredicateResult>? predicateResults = null,
        PredicateCombinationRule? combinationRule = null)
    {
        var evidenceMode = options.JudgeEvidenceMode;
        var normalized = raw?.Trim();
        var boundedRaw = normalized is { Length: > 4096 } ? normalized[..4096] : normalized;

        // Under the structured protocol the reasoning field is the readable half of the response, so it
        // is preferred over the raw JSON when an explanation is being rendered.
        var explanationBody = !string.IsNullOrEmpty(reasoning) ? reasoning : boundedRaw;
        var explanation = evidenceMode switch
        {
            JudgeEvidenceMode.None => null,
            JudgeEvidenceMode.Outcome => $"Judge outcome: {status}",
            _ when !string.IsNullOrEmpty(explanationBody) => $"Judge said: {explanationBody}",
            _ => $"Judge outcome: {status}"
        };

        return new ExternalJudgmentResult
        {
            Status = status,
            Correct = status == JudgeOutcomeStatus.Yes
                ? true
                : status == JudgeOutcomeStatus.No ? false : null,
            RawScore = status == JudgeOutcomeStatus.Yes
                ? 100
                : status == JudgeOutcomeStatus.No ? 0 : null,
            Explanation = explanation,
            TokensUsed = totalTokens,
            LlmCallCount = providerCalls,
            SafeFailureCode = safeFailureCode,
            RawResponse = evidenceMode == JudgeEvidenceMode.Raw || options.RetainRawJudgeResponse
                ? boundedRaw
                : null,
            Reasoning = reasoning,
            PredicateResults = predicateResults,
            PredicateCombinationRule = combinationRule
        };
    }

    /// <summary>
    /// Selects the appropriate judge prompt template based on question type.
    /// Abstention is detected by _abs suffix in question_id (cross-type concern).
    /// </summary>
    internal static string SelectPrompt(ExternalBenchmarkQuestion question, string hypothesis)
    {
        // Abstention takes priority — it's a cross-type concern identified by question_id
        if (question.IsAbstention)
            return LongMemEvalJudgePrompts.Abstention(question.Question, question.GoldAnswer, hypothesis);

        return question.QuestionType switch
        {
            "single-session-preference" =>
                LongMemEvalJudgePrompts.Preference(question.Question, question.GoldAnswer, hypothesis),

            "temporal-reasoning" =>
                LongMemEvalJudgePrompts.Temporal(question.Question, question.GoldAnswer, hypothesis),

            "knowledge-update" =>
                LongMemEvalJudgePrompts.KnowledgeUpdate(question.Question, question.GoldAnswer, hypothesis),

            // single-session-user, single-session-assistant, multi-session, and any unknown types
            _ => LongMemEvalJudgePrompts.Standard(question.Question, question.GoldAnswer, hypothesis)
        };
    }
}
