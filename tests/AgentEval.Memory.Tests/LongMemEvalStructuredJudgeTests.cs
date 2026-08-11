// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Tests for <see cref="JudgeVerdictProtocol.StructuredJson"/>: the verdict comes from a closed-set
/// field, reasoning lives in its own field, and anything unusable is
/// <see cref="JudgeOutcomeStatus.Invalid"/> rather than an exception, a silent No, or a guess.
/// </summary>
public class LongMemEvalStructuredJudgeTests
{
    /// <summary>
    /// The reasoning text that breaks the free-text protocol. Held in one constant and used by both the
    /// free-text and structured cases below, so the two protocols are compared on identical content.
    /// </summary>
    private const string ReasoningContainingTheWordNo =
        "The model response identifies the correct date, and there is no discrepancy with the correct answer.";

    [Fact]
    public async Task FreeTextProtocol_YesWhoseReasoningContainsNo_IsInvalid_TheDefectBeingFixed()
    {
        var judge = CreateJudge(Response($"Yes. {ReasoningContainingTheWordNo}"));

        var result = await judge.JudgeAsync("answer", Question(), FreeTextOptions());

        // Baseline: this is what the consumer observes today — an unjudgeable question.
        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
    }

    [Fact]
    public async Task StructuredProtocol_SameReasoningText_ProducesADefiniteVerdict()
    {
        var judge = CreateJudge(Response(
            $$"""{"verdict": "yes", "reasoning": "{{ReasoningContainingTheWordNo}}"}"""));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        // The fix: identical reasoning, definite verdict, because the verdict was never recovered
        // from the reasoning in the first place.
        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        Assert.True(result.Correct);
        Assert.Equal(100, result.RawScore);
        Assert.Equal(ReasoningContainingTheWordNo, result.Reasoning);
    }

    [Theory]
    [InlineData("no", JudgeOutcomeStatus.No, false, 0d)]
    [InlineData("yes", JudgeOutcomeStatus.Yes, true, 100d)]
    public async Task StructuredProtocol_ClosedSetVerdicts_MapToScoredOutcomes(
        string verdict,
        JudgeOutcomeStatus expectedStatus,
        bool expectedCorrect,
        double expectedScore)
    {
        var judge = CreateJudge(Response($$"""{"verdict": "{{verdict}}", "reasoning": "because"}"""));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCorrect, result.Correct);
        Assert.Equal(expectedScore, result.RawScore);
    }

    /// <summary>
    /// Every shape of unusable structured response. None may throw, none may become a silent
    /// <see cref="JudgeOutcomeStatus.No"/>, and none may be guessed at from surrounding prose.
    /// </summary>
    public static TheoryData<string, string> UnusableResponses() => new()
    {
        { "The response looks correct to me.", "structured_no_json" },
        { """{"verdict": "maybe", "reasoning": "unsure"}""", "structured_verdict_out_of_enum" },
        { """{"verdict": true, "reasoning": "typed wrong"}""", "structured_verdict_not_string" },
        { """{"reasoning": "forgot the verdict"}""", "structured_missing_verdict" },
        { """{"verdict": "yes",""", "structured_no_json" },
        { """["yes"]""", "structured_no_json" },
    };

    [Theory]
    [MemberData(nameof(UnusableResponses))]
    public async Task StructuredProtocol_UnusableResponse_IsInvalidWithADiagnosticCode(
        string unusable,
        string expectedFailureCode)
    {
        var judge = CreateJudge(Response(unusable));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        Assert.Null(result.RawScore);
        Assert.Equal(expectedFailureCode, result.SafeFailureCode);
    }

    [Fact]
    public async Task StructuredProtocol_CannotDetermine_IsInvalidAndDistinguishableFromNo()
    {
        var judge = CreateJudge(Response(
            """{"verdict": "cannot-determine", "reasoning": "The gold answer is ambiguous."}"""));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        // Invalid, not No: an unjudgeable question stays visibly unjudged.
        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
        // Its own code, so "the judge declined" is separable from "the wrapper could not parse".
        Assert.Equal("judge_cannot_determine", result.SafeFailureCode);
        Assert.Equal("The gold answer is ambiguous.", result.Reasoning);
    }

    [Theory]
    [InlineData(JudgeFailurePolicy.RetryThenInconclusive)]
    [InlineData(JudgeFailurePolicy.RetryThenIncorrect)]
    public async Task StructuredProtocol_InvalidUnderEitherRetryPolicy_StaysInvalidNotNo(
        JudgeFailurePolicy policy)
    {
        var judge = CreateJudge(Response("not json"), Response("still not json"));

        var result = await judge.JudgeAsync(
            "answer",
            Question(),
            new ExternalBenchmarkOptions
            {
                JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
                JudgeFailurePolicy = policy,
                MaxJudgeRetries = 1
            });

        // RetryThenIncorrect is an aggregation choice made downstream; the judge must not pre-collapse
        // it, or Invalid and No become indistinguishable at the source.
        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Null(result.Correct);
    }

    [Fact]
    public async Task StructuredProtocol_FencedJsonWithSurroundingProse_IsParsed()
    {
        var judge = CreateJudge(Response(
            """
            Here is my assessment:
            ```json
            {"verdict": "no", "reasoning": "The response omits the required date."}
            ```
            Let me know if you need more detail.
            """));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(JudgeOutcomeStatus.No, result.Status);
        Assert.Equal("The response omits the required date.", result.Reasoning);
    }

    [Fact]
    public async Task StructuredProtocol_RequestsSchemaConstrainedOutput()
    {
        var client = new RecordingChatClient(Response("""{"verdict": "yes", "reasoning": "ok"}"""));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        await judge.JudgeAsync("answer", Question(), StructuredOptions());

        var format = Assert.IsType<ChatResponseFormatJson>(client.LastOptions!.ResponseFormat);
        Assert.Equal("longmemeval_judge_verdict", format.SchemaName);
        Assert.NotNull(format.Schema);
        // The prompt carries the contract too, so a provider that ignores response_format can comply.
        Assert.Contains("cannot-determine", client.LastPrompt!, StringComparison.Ordinal);
        // ...and the contradictory free-text instruction is gone.
        Assert.DoesNotContain("Answer yes or no only.", client.LastPrompt!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredProtocol_ProviderRejectsResponseFormat_FallsBackAndStillJudges()
    {
        var judge = CreateJudge(
            () => throw new InvalidOperationException(
                "400 invalid_request_error: response_format json_schema is not supported by this model"),
            () => throw new InvalidOperationException(
                "400 invalid_request_error: response_format json_object is not supported by this model"),
            Response("""{"verdict": "yes", "reasoning": "recovered unconstrained"}"""));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(JudgeOutcomeStatus.Yes, result.Status);
        // All three provider calls are counted; the two rejected ones were still paid for.
        Assert.Equal(3, result.LlmCallCount);
    }

    [Fact]
    public async Task StructuredProtocol_GenuineProviderFailure_IsNotMistakenForACapabilityProblem()
    {
        var judge = CreateJudge(() => throw new InvalidOperationException("upstream connect error"));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        // A network error must not be retried as though the model lacked structured output.
        Assert.Equal(JudgeOutcomeStatus.ProviderError, result.Status);
        Assert.Equal("provider_error", result.SafeFailureCode);
        Assert.Equal(1, result.LlmCallCount);
    }

    [Theory]
    [InlineData("length", "invalid_finish_reason")]
    [InlineData("content_filter", "content_filtered")]
    public async Task StructuredProtocol_TruncatedOrFiltered_IsInvalidEvenIfTheJsonLooksComplete(
        string finishReason,
        string expectedCode)
    {
        var judge = CreateJudge(Response(
            """{"verdict": "yes", "reasoning": "ok"}""",
            new ChatFinishReason(finishReason)));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Equal(expectedCode, result.SafeFailureCode);
    }

    [Fact]
    public async Task StructuredProtocol_EmptyResponse_IsEmptyNotInvalid()
    {
        var judge = CreateJudge(Response("   "));

        var result = await judge.JudgeAsync("answer", Question(), StructuredOptions());

        Assert.Equal(JudgeOutcomeStatus.Empty, result.Status);
        Assert.Equal("empty_response", result.SafeFailureCode);
    }

    [Fact]
    public async Task FreeTextProtocol_DoesNotRequestAResponseFormat()
    {
        var client = new RecordingChatClient(Response("yes"));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        await judge.JudgeAsync("answer", Question(), FreeTextOptions());

        // The default path is untouched: same single unconstrained call as before.
        Assert.Null(client.LastOptions!.ResponseFormat);
        Assert.Equal(1, client.CallCount);
    }

    internal static LongMemEvalJudge CreateJudge(params Func<ChatResponse>[] sequence)
        => new(new RecordingChatClient(sequence), NullLogger<LongMemEvalJudge>.Instance);

    internal static Func<ChatResponse> Response(string text, ChatFinishReason? finishReason = null)
        => () => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = finishReason };

    private static ExternalBenchmarkOptions StructuredOptions() => new()
    {
        JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
        MaxJudgeRetries = 0
    };

    private static ExternalBenchmarkOptions FreeTextOptions() => new()
    {
        JudgeVerdictProtocol = JudgeVerdictProtocol.FreeText,
        MaxJudgeRetries = 0
    };

    internal static ExternalBenchmarkQuestion Question(
        string type = "single-session-user",
        string goldAnswer = "The expected answer") => new()
    {
        QuestionId = "q-1",
        QuestionType = type,
        Question = "What did the user say?",
        GoldAnswer = goldAnswer
    };

    /// <summary>Chat client that replays a scripted sequence and records what it was asked.</summary>
    internal sealed class RecordingChatClient(params Func<ChatResponse>[] sequence) : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _sequence = new(sequence);

        public ChatOptions? LastOptions { get; private set; }

        public string? LastPrompt { get; private set; }

        public int CallCount { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("recording", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastPrompt = string.Join("\n", chatMessages.Select(m => m.Text));
            CallCount++;
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
