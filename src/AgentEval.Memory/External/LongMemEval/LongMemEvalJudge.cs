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
    public async Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentResponse);
        ArgumentNullException.ThrowIfNull(question);

        var judgePrompt = SelectPrompt(question, agentResponse);

        try
        {
            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, judgePrompt)],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 30 },
                ct);

            var responseText = response.Text?.Trim().ToLowerInvariant() ?? "";
            var correct = responseText.Contains("yes");

            var tokensUsed = (int)(response.Usage?.TotalTokenCount ?? 0);

            _logger.LogDebug(
                "LongMemEval judge [{QuestionId}] type={Type} correct={Correct} raw=\"{Raw}\"",
                question.QuestionId, question.QuestionType, correct, responseText);

            return new ExternalJudgmentResult
            {
                Correct = correct,
                RawScore = correct ? 100 : 0,
                Explanation = $"Judge said: {responseText}",
                TokensUsed = tokensUsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Judge call failed for question {QuestionId}", question.QuestionId);

            return new ExternalJudgmentResult
            {
                Correct = false,
                RawScore = 0,
                Explanation = $"Judge error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Selects the appropriate judge prompt template based on question type.
    /// Abstention is detected by _abs suffix in question_id (cross-type concern).
    /// </summary>
    internal static string SelectPrompt(ExternalBenchmarkQuestion question, string hypothesis)
    {
        // Abstention takes priority — it's a cross-type concern identified by question_id
        if (question.IsAbstention)
            return LongMemEvalJudgePrompts.Abstention(question.Question, hypothesis);

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
