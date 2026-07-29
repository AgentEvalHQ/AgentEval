// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text.Json;

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests;

public class LongMemEvalJudgeTests
{
    [Theory]
    [InlineData("yes", JudgeOutcomeStatus.Yes, true, 100)]
    [InlineData("No.", JudgeOutcomeStatus.No, false, 0)]
    public async Task JudgeAsync_ExplicitBinaryResponse_ReturnsScoredOutcome(
        string text,
        JudgeOutcomeStatus expectedStatus,
        bool expectedCorrect,
        double expectedScore)
    {
        var judge = CreateJudge(Response(text));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCorrect, result.Correct);
        Assert.Equal(expectedScore, result.RawScore);
        Assert.Equal(1, result.LlmCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public async Task JudgeAsync_EmptyResponse_ReturnsInconclusiveNeverIncorrect(string text)
    {
        var judge = CreateJudge(Response(text));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(JudgeOutcomeStatus.Empty, result.Status);
        Assert.Null(result.Correct);
        Assert.Null(result.RawScore);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("the answer is yes")]
    [InlineData("yes, but no")]
    public async Task JudgeAsync_MalformedResponse_ReturnsInvalidNeverIncorrect(string text)
    {
        var judge = CreateJudge(Response(text));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        Assert.Null(result.RawScore);
    }

    [Fact]
    public async Task JudgeAsync_TruncatedResponse_ReturnsInvalidEvenWhenTextStartsYes()
    {
        var judge = CreateJudge(Response("yes", ChatFinishReason.Length));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        Assert.Equal("invalid_finish_reason", result.SafeFailureCode);
    }

    [Fact]
    public async Task JudgeAsync_ContentFilteredResponse_ReturnsOwnedFailureCode()
    {
        var judge = CreateJudge(Response("yes", ChatFinishReason.ContentFilter));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        Assert.Equal("content_filtered", result.SafeFailureCode);
    }

    [Fact]
    public async Task JudgeAsync_ProviderException_ReturnsSafeProviderError()
    {
        var judge = CreateJudge(() => throw new InvalidOperationException("secret provider detail"));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 0));

        Assert.Equal(JudgeOutcomeStatus.ProviderError, result.Status);
        Assert.Null(result.Correct);
        Assert.Equal("provider_error", result.SafeFailureCode);
        Assert.DoesNotContain("secret provider detail", result.Explanation ?? string.Empty);
    }

    [Fact]
    public async Task JudgeAsync_InconclusiveThenYes_RetriesAndCountsCallsExactlyOnce()
    {
        var judge = CreateJudge(Response(" "), Response("yes"));

        var result = await judge.JudgeAsync("answer", Question(), Options(maxRetries: 1));

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        Assert.True(result.Correct);
        Assert.Equal(2, result.LlmCallCount);
        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task JudgeAsync_Cancellation_Propagates()
    {
        var judge = CreateJudge(() => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => judge.JudgeAsync("answer", Question(), Options(maxRetries: 1)));
    }

    [Fact]
    public async Task JudgeAsync_RawEvidenceMode_IsExplicitAndBounded()
    {
        var judge = CreateJudge(Response("yes because it matches"));
        var options = new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.Raw
        };

        var result = await judge.JudgeAsync("answer", Question(), options);

        Assert.Equal("yes because it matches", result.RawResponse);
    }

    [Theory]
    [InlineData(true, 100, JudgeOutcomeStatus.Yes)]
    [InlineData(false, 0, JudgeOutcomeStatus.No)]
    public void ExternalJudgmentResult_LegacySuccessfulJson_InfersTypedStatus(
        bool correct,
        double rawScore,
        JudgeOutcomeStatus expected)
    {
        var json = $$"""{"Correct":{{correct.ToString().ToLowerInvariant()}},"RawScore":{{rawScore}}}""";

        var result = JsonSerializer.Deserialize<ExternalJudgmentResult>(json);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Status);
        Assert.Equal(correct, result.Correct);
        Assert.Equal(rawScore, result.RawScore);
    }

    [Fact]
    public void QuestionResult_LegacySuccessfulJson_InfersTypedStatus()
    {
        const string json =
            """{"QuestionId":"q","QuestionType":"single-session-user","Question":"q?","GoldAnswer":"g","AgentResponse":"a","Correct":true,"RawScore":100}""";

        var result = JsonSerializer.Deserialize<QuestionResult>(json);

        Assert.NotNull(result);
        Assert.Equal(JudgeOutcomeStatus.Yes, result!.JudgeStatus);
    }

    [Fact]
    public void TypeResult_Accuracy_PreservesZeroToOneHundredScale()
    {
        var result = new TypeResult
        {
            TypeName = "single-session-user",
            TotalQuestions = 5,
            ScoredQuestions = 5,
            CorrectQuestions = 2,
            Duration = TimeSpan.Zero
        };

        Assert.Equal(40, result.Accuracy);
    }

    [Fact]
    public void TypeResult_LegacyJsonWithoutScoredCount_UsesSelectedDenominator()
    {
        const string json =
            """{"TypeName":"single-session-user","TotalQuestions":5,"CorrectQuestions":2,"Duration":"00:00:00"}""";

        var result = JsonSerializer.Deserialize<TypeResult>(json);

        Assert.NotNull(result);
        Assert.Equal(5, result!.ScoredQuestions);
        Assert.Equal(40, result.Accuracy);
    }

    [Fact]
    public async Task JudgeAsync_EvidenceNone_OmitsRawResponseFromJson()
    {
        var judge = CreateJudge(Response("yes because it matches"));
        var options = new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.None
        };

        var result = await judge.JudgeAsync("answer", Question(), options);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("RawResponse", json);
    }

    [Fact]
    public void SelectPrompt_Preference_RetainsOfficialFlexibleCriterion()
    {
        var question = Question("single-session-preference");

        var prompt = LongMemEvalJudge.SelectPrompt(question, "personalized response");

        Assert.Contains("does not need to reflect all the points", prompt);
        Assert.Contains("recalls and utilizes the user's personal information correctly", prompt);
    }

    [Theory]
    [InlineData(
        "d24813b1",
        "What should I bake?",
        "Build on the user's successful lemon poppyseed cake and keep the plan manageable.",
        "Make the lemon poppyseed cake again and add a simple cookie.")]
    [InlineData(
        "preference-travel",
        "Where should I stay?",
        "The user prefers quiet, walkable neighborhoods over nightlife.",
        "Choose a quiet residential area within walking distance of the center.")]
    [InlineData(
        "preference-food",
        "What dinner should I make?",
        "The user avoids shellfish and likes spicy vegetarian food.",
        "Make a spicy vegetarian curry and avoid shellfish.")]
    public void SelectPrompt_PreferenceCorpus_UsesGeneralRubricWithoutQuestionSpecificLogic(
        string questionId,
        string questionText,
        string rubric,
        string response)
    {
        var question = Question("single-session-preference", questionId, questionText, rubric);

        var prompt = LongMemEvalJudge.SelectPrompt(question, response);

        Assert.Contains(questionText, prompt);
        Assert.Contains(rubric, prompt);
        Assert.Contains(response, prompt);
        Assert.Contains("does not need to reflect all the points", prompt);
    }

    [Fact]
    public void SelectPrompt_Standard_RetainsOfficialEquivalenceCriterion()
    {
        var prompt = LongMemEvalJudge.SelectPrompt(Question(), "equivalent response");

        Assert.Contains(
            "equivalent to the correct answer or contains all the intermediate steps",
            prompt);
        Assert.Contains("If the response only contains a subset", prompt);
    }

    [Fact]
    public void SelectPrompt_Temporal_RetainsOfficialEquivalenceAndOffByOneCriteria()
    {
        var prompt = LongMemEvalJudge.SelectPrompt(
            Question("temporal-reasoning"),
            "temporal response");

        Assert.Contains(
            "equivalent to the correct answer or contains all the intermediate steps",
            prompt);
        Assert.Contains("do not penalize off-by-one errors", prompt);
    }

    [Fact]
    public void SelectPrompt_KnowledgeUpdate_RetainsOfficialUpdatedAnswerCriterion()
    {
        var prompt = LongMemEvalJudge.SelectPrompt(
            Question("knowledge-update"),
            "updated response");

        Assert.Contains("previous information along with an updated answer", prompt);
        Assert.Contains("updated answer is the required answer", prompt);
    }

    [Fact]
    public void SelectPrompt_Abstention_IncludesOfficialExplanationField()
    {
        var question = new ExternalBenchmarkQuestion
        {
            QuestionId = "q-abs",
            QuestionType = "single-session-user",
            Question = "What never happened?",
            GoldAnswer = "The history contains no such event.",
            IsAbstention = true
        };

        var prompt = LongMemEvalJudge.SelectPrompt(question, "I cannot answer that.");

        Assert.Contains("Explanation: The history contains no such event.", prompt);
        Assert.Contains("unanswerable question, an explanation", prompt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Validate_OutOfRangeJudgeRetries_Throws(int retries)
    {
        var options = new ExternalBenchmarkOptions { MaxJudgeRetries = retries };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    private static LongMemEvalJudge CreateJudge(params Func<ChatResponse>[] sequence)
        => new(new SequenceChatClient(sequence), NullLogger<LongMemEvalJudge>.Instance);

    private static Func<ChatResponse> Response(string text, ChatFinishReason? finishReason = null)
        => () => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = finishReason
        };

    private static ExternalBenchmarkOptions Options(int maxRetries) => new()
    {
        MaxJudgeRetries = maxRetries,
        JudgeFailurePolicy = JudgeFailurePolicy.RetryThenInconclusive,
        JudgeEvidenceMode = JudgeEvidenceMode.Outcome
    };

    private static ExternalBenchmarkQuestion Question(
        string type = "single-session-user",
        string questionId = "q-1",
        string questionText = "What did the user say?",
        string goldAnswer = "The expected answer") => new()
    {
        QuestionId = questionId,
        QuestionType = type,
        Question = questionText,
        GoldAnswer = goldAnswer
    };

    private sealed class SequenceChatClient(params Func<ChatResponse>[] sequence) : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _sequence = new(sequence);

        public ChatClientMetadata Metadata { get; } = new("sequence", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_sequence.Count == 0)
                throw new InvalidOperationException("No response configured.");
            return Task.FromResult(_sequence.Dequeue().Invoke());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
