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

        var judgePrompt = SelectPrompt(question, agentResponse);
        var totalTokens = 0;
        var attempts = 0;
        ExternalJudgmentResult? lastResult = null;

        while (attempts <= options.MaxJudgeRetries)
        {
            attempts++;
            try
            {
                var response = await _chatClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, judgePrompt)],
                    new ChatOptions
                    {
                        Temperature = (float?)options.JudgeTemperature,
                        MaxOutputTokens = options.JudgeMaxOutputTokens
                    },
                    ct).ConfigureAwait(false);

                totalTokens += (int)(response.Usage?.TotalTokenCount ?? 0);
                var finishReason = response.FinishReason?.Value;
                var status = ParseResponse(response.Text, finishReason);
                var safeCode = finishReason?.ToLowerInvariant() switch
                {
                    "content_filter" => "content_filtered",
                    "length" => "invalid_finish_reason",
                    _ when status == JudgeOutcomeStatus.Empty => "empty_response",
                    _ when status == JudgeOutcomeStatus.Invalid => "invalid_response",
                    _ => null
                };
                lastResult = CreateResult(
                    status, response.Text, attempts, totalTokens, options.JudgeEvidenceMode, safeCode);
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
                lastResult = CreateResult(
                    JudgeOutcomeStatus.ProviderError,
                    raw: null,
                    attempts,
                    totalTokens,
                    options.JudgeEvidenceMode,
                    safeCode);
            }

            if (lastResult.Status is JudgeOutcomeStatus.Yes or JudgeOutcomeStatus.No)
                return lastResult;

            if (options.JudgeFailurePolicy == JudgeFailurePolicy.FailRun)
            {
                throw new LongMemEvalJudgeException(question.QuestionId, lastResult.Status);
            }
        }

        return lastResult!;
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
        int attempts,
        int totalTokens,
        JudgeEvidenceMode evidenceMode,
        string? safeFailureCode = null)
    {
        var normalized = raw?.Trim();
        var boundedRaw = normalized is { Length: > 4096 } ? normalized[..4096] : normalized;
        var explanation = evidenceMode switch
        {
            JudgeEvidenceMode.None => null,
            JudgeEvidenceMode.Outcome => $"Judge outcome: {status}",
            _ when !string.IsNullOrEmpty(boundedRaw) => $"Judge said: {boundedRaw}",
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
            LlmCallCount = attempts,
            SafeFailureCode = safeFailureCode,
            RawResponse = evidenceMode == JudgeEvidenceMode.Raw ? boundedRaw : null
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
